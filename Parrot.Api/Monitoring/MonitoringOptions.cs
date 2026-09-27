namespace Parrot.Api.Monitoring;

public sealed class MonitoringOptions
{
    public const string SectionName = "Monitoring";

    /// <summary>A request slower than this, with no [Monitor(SlowMs)] of its own, counts as slow.</summary>
    public int DefaultSlowMs { get; init; } = 1000;

    /// <summary>
    /// Key for GET /api/monitoring, sent as the X-Monitoring-Key header. Unset
    /// means the endpoint answers 404, except in Development where it is open.
    /// </summary>
    public string? ApiKey { get; init; }
}
