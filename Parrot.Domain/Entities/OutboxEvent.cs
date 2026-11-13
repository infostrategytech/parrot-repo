namespace Parrot.Domain.Entities;

/// <summary>
/// Durable outbox row for a BusinessSourcesUpdated Kafka event.
/// The <see cref="Id"/> doubles as the <c>event_id</c> in the Kafka envelope so
/// consumers can use it for idempotency. Kafka publish happens OUTSIDE the claim
/// transaction; <see cref="LockedUntil"/> prevents double-publish if the worker restarts.
/// </summary>
public class OutboxEvent
{
    /// <summary>Also the event_id in the Kafka envelope.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Kafka partition key.</summary>
    public required string WorkspaceId { get; set; }

    /// <summary>Serialized <c>BusinessSourcesUpdatedMessage</c> (JSON, snake_case).</summary>
    public required string Payload { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? PublishedAt { get; set; }

    /// <summary>
    /// Set by the outbox worker before publishing. If the worker crashes after publishing
    /// but before marking done, the row becomes reclaimable once this timestamp elapses.
    /// </summary>
    public DateTime? LockedUntil { get; set; }

    public int AttemptCount { get; set; }

    /// <summary>Backoff-scheduled next attempt time. Null means eligible immediately.</summary>
    public DateTime? NextAttemptAt { get; set; }

    public string? LastError { get; set; }
}
