using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Options;

namespace Parrot.Api.Monitoring;

/// <summary>
/// Times every request and records its outcome. It sits outermost, outside
/// the exception handler and the rate limiter, so the status it sees is the
/// one the client got: handled exceptions as 500s, throttled calls as 429s,
/// unknown paths as 404s. [Monitor] on the endpoint only names and tunes it.
/// </summary>
public sealed class MonitoringMiddleware
{
    private const string CorrelationIdHeader = "X-Correlation-ID";
    private const int ClientClosedRequest = 499;

    private readonly RequestDelegate _next;
    private readonly RequestMetrics _metrics;
    private readonly MonitoringStore _store;
    private readonly ILogger<MonitoringMiddleware> _logger;
    private readonly MonitoringOptions _options;
    private readonly ConcurrentDictionary<Endpoint, Descriptor> _descriptors = new();

    public MonitoringMiddleware(
        RequestDelegate next,
        RequestMetrics metrics,
        MonitoringStore store,
        IOptions<MonitoringOptions> options,
        ILogger<MonitoringMiddleware> logger)
    {
        _next = next;
        _metrics = metrics;
        _store = store;
        _options = options.Value;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        long started = Stopwatch.GetTimestamp();

        // Server-Timing shows the server's share of a request in the
        // browser's network panel. It has to be set before the body starts.
        context.Response.OnStarting(() =>
        {
            double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            context.Response.Headers.Append("Server-Timing", "app;dur=" + ms.ToString("0.0", CultureInfo.InvariantCulture));
            return Task.CompletedTask;
        });

        bool threw = false;
        try
        {
            await _next(context);
        }
        catch
        {
            threw = true;
            throw;
        }
        finally
        {
            Record(context, Stopwatch.GetElapsedTime(started).TotalMilliseconds, threw);
        }
    }

    private void Record(HttpContext context, double elapsedMs, bool threw)
    {
        Descriptor descriptor = Describe(context);
        if (descriptor.Ignore) return;

        bool aborted = context.RequestAborted.IsCancellationRequested && !context.Response.HasStarted;
        int status = threw && !context.Response.HasStarted ? StatusCodes.Status500InternalServerError
            : aborted ? ClientClosedRequest
            : context.Response.StatusCode;

        RequestSample sample = new RequestSample(
            descriptor.Operation,
            context.Request.Method,
            status,
            elapsedMs,
            elapsedMs > descriptor.SlowMs,
            Outcomes.For(status, aborted),
            DateTimeOffset.UtcNow,
            context.Response.Headers[CorrelationIdHeader].FirstOrDefault());

        _metrics.Record(sample);
        _store.Record(sample);

        if (sample.StatusCode >= 500)
        {
            _logger.LogError(
                "Request failed {Operation} {HttpMethod} -> {StatusCode} in {ElapsedMs:0.0}ms. CorrelationId: {CorrelationId}",
                sample.Operation, sample.Method, sample.StatusCode, sample.ElapsedMs, sample.CorrelationId);
        }
        else if (sample.IsSlow)
        {
            _logger.LogWarning(
                "Slow request {Operation} {HttpMethod} -> {StatusCode} in {ElapsedMs:0.0}ms (threshold {SlowMs}ms). CorrelationId: {CorrelationId}",
                sample.Operation, sample.Method, sample.StatusCode, sample.ElapsedMs, descriptor.SlowMs, sample.CorrelationId);
        }
    }

    private Descriptor Describe(HttpContext context)
    {
        Endpoint? endpoint = context.GetEndpoint();

        // Anything routing did not match is one bucket: scanners probing random
        // paths must not mint a new metric series per URL.
        if (endpoint is null) return new Descriptor("unmatched", _options.DefaultSlowMs, false);

        return _descriptors.GetOrAdd(endpoint, e => Build(e, _options.DefaultSlowMs));
    }

    public static Descriptor Build(Endpoint endpoint, int defaultSlowMs)
    {
        ControllerActionDescriptor? action = endpoint.Metadata.GetMetadata<ControllerActionDescriptor>();
        MonitorAttribute? onAction = action?.MethodInfo.GetCustomAttribute<MonitorAttribute>(inherit: true);
        MonitorAttribute? onController = action?.ControllerTypeInfo.GetCustomAttribute<MonitorAttribute>(inherit: true);

        if (onAction?.Ignore == true || (onController?.Ignore == true && onAction is null))
        {
            return new Descriptor(string.Empty, defaultSlowMs, true);
        }

        int slowMs = onAction?.SlowMs > 0 ? onAction.SlowMs
            : onController?.SlowMs > 0 ? onController.SlowMs
            : defaultSlowMs;

        string name = onAction?.Name
            ?? (action is null ? Fallback(endpoint) : Join(onAction?.Area ?? onController?.Area ?? Kebab(action.ControllerName), Kebab(action.ActionName)));

        return new Descriptor(name, slowMs, false);
    }

    private static string Fallback(Endpoint endpoint) =>
        (endpoint as RouteEndpoint)?.RoutePattern.RawText ?? endpoint.DisplayName ?? "unknown";

    private static string Join(string area, string action) => $"{area}.{action}";

    // "ResendVerificationEmailAsync" -> "resend-verification-email".
    private static string Kebab(string name)
    {
        if (name.EndsWith("Async", StringComparison.Ordinal)) name = name[..^5];
        System.Text.StringBuilder chars = new System.Text.StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsUpper(c) && i > 0 && !char.IsUpper(name[i - 1])) chars.Append('-');
            chars.Append(char.ToLowerInvariant(c));
        }

        return chars.ToString();
    }

    public readonly record struct Descriptor(string Operation, int SlowMs, bool Ignore);
}
