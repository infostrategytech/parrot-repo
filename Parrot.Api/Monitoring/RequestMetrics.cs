using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Parrot.Api.Monitoring;

/// <summary>
/// The instruments, on a Meter named "Parrot.Api", so anything that reads
/// .NET metrics can watch them without code changes: `dotnet-counters monitor
/// -n Parrot.Api.Rest --counters Parrot.Api`, or an OpenTelemetry/Prometheus
/// exporter subscribed to the meter. Tags are low-cardinality on purpose:
/// operation (a fixed name, never a raw path), method, status code, class and
/// outcome.
/// </summary>
public sealed class RequestMetrics : IDisposable
{
    public const string MeterName = "Parrot.Api";

    private readonly Meter _meter;
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _requests;
    private readonly Counter<long> _errors;
    private readonly Counter<long> _slow;

    public RequestMetrics(IMeterFactory meterFactory)
    {
        _meter = meterFactory.Create(MeterName);
        _duration = _meter.CreateHistogram<double>("parrot.http.server.duration", "ms", "Time from request received to response completed.");
        _requests = _meter.CreateCounter<long>("parrot.http.server.requests", "{request}", "Requests handled, by operation and status.");
        _errors = _meter.CreateCounter<long>("parrot.http.server.errors", "{request}", "Requests that ended in a 5xx or were aborted.");
        _slow = _meter.CreateCounter<long>("parrot.http.server.slow", "{request}", "Requests slower than their operation's threshold.");
    }

    public void Record(RequestSample sample)
    {
        TagList tags = new TagList
        {
            { "operation", sample.Operation },
            { "method", sample.Method },
            { "status_code", sample.StatusCode },
            { "status_class", sample.StatusClass },
            { "outcome", sample.Outcome },
        };

        _duration.Record(sample.ElapsedMs, tags);
        _requests.Add(1, tags);
        if (sample.IsError) _errors.Add(1, tags);
        if (sample.IsSlow) _slow.Add(1, tags);
    }

    public void Dispose() => _meter.Dispose();
}

public readonly record struct RequestSample(
    string Operation,
    string Method,
    int StatusCode,
    double ElapsedMs,
    bool IsSlow,
    string Outcome,
    DateTimeOffset At,
    string? CorrelationId)
{
    public string StatusClass => StatusCode switch
    {
        >= 500 => "5xx",
        >= 400 => "4xx",
        >= 300 => "3xx",
        >= 200 => "2xx",
        _ => "1xx",
    };

    public bool IsError => StatusCode >= 500 || Outcome == Outcomes.Aborted;
}

public static class Outcomes
{
    public const string Success = "success";
    public const string ClientError = "client_error";
    public const string ServerError = "server_error";
    public const string Aborted = "aborted";

    public static string For(int statusCode, bool aborted) =>
        aborted ? Aborted : statusCode >= 500 ? ServerError : statusCode >= 400 ? ClientError : Success;
}
