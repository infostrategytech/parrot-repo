namespace Parrot.Application.Realtime;

/// <summary>
/// Redis holds who is online right now, and nothing else: no message history,
/// no undelivered messages, no delivery state. Clearing it loses nothing that
/// MongoDB does not already have; connected clients simply register again.
/// </summary>
public class RedisSettings
{
    public const string SectionName = "Redis";

    public string ConnectionString { get; set; } = "localhost:6379";

    /// <summary>
    /// How long a session lives without a heartbeat. A client that vanishes
    /// without disconnecting drops off the online list once this runs out.
    /// </summary>
    public int SessionTtlSeconds { get; set; } = 60;

    /// <summary>How often a connected client renews its session. Keep it well under the TTL.</summary>
    public int HeartbeatIntervalSeconds { get; set; } = 20;
}
