using System.Diagnostics;
using Parrot.Application.Logging;

namespace Parrot.Api.Middleware;

public class RequestContextMiddleware
{
    private const string CorrelationIdHeader = "X-Correlation-ID";
    private static readonly int MaxCorrelationIdLength = 128;

    private readonly RequestDelegate _next;
    private readonly ILogger<RequestContextMiddleware> _logger;

    public RequestContextMiddleware(RequestDelegate next, ILogger<RequestContextMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, RequestContext requestContext)
    {
        string correlationId = ResolveCorrelationId(context);

        requestContext.CorrelationId = correlationId;
        requestContext.IpAddress = context.Connection.RemoteIpAddress?.ToString();
        requestContext.HttpMethod = context.Request.Method;
        requestContext.RequestPath = context.Request.Path.Value;
        requestContext.UserAgent = context.Request.Headers.UserAgent.ToString();

        context.Response.Headers.TryAdd(CorrelationIdHeader, correlationId);

        Stopwatch stopwatch = Stopwatch.StartNew();

        // Only the correlation id goes on every line of the request; the trace id
        // and request path are already added by ASP.NET Core. A message-template
        // scope, not a dictionary, so the console prints its value rather than
        // its type name.
        using (_logger.BeginScope("CorrelationId:{CorrelationId}", correlationId))
        {
            _logger.LogInformation("HTTP {HttpMethod} {RequestPath} started", requestContext.HttpMethod, requestContext.RequestPath);

            await _next(context);

            stopwatch.Stop();
            _logger.LogInformation(
                "HTTP {HttpMethod} {RequestPath} completed {StatusCode} in {ElapsedMs}ms",
                requestContext.HttpMethod,
                requestContext.RequestPath,
                context.Response.StatusCode,
                stopwatch.ElapsedMilliseconds);
        }
    }

    private static string ResolveCorrelationId(HttpContext context)
    {
        string? incoming = context.Request.Headers[CorrelationIdHeader];

        if (!string.IsNullOrEmpty(incoming)
            && incoming.Length <= MaxCorrelationIdLength
            && incoming.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_'))
        {
            return incoming;
        }

        // The request's trace id, so a caller who sends none sees the id that
        // every log line of this request and its messages carries.
        return Activity.Current?.TraceId.ToHexString() ?? Guid.NewGuid().ToString();
    }
}
