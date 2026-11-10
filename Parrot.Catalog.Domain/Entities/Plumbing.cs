namespace Parrot.Catalog.Domain.Entities;

/// <summary>Append-only at the grant level: the writer role has INSERT and SELECT only.</summary>
public sealed class AuditLogEntry : ITenantScoped
{
    public long Id { get; set; }

    public Guid TenantId { get; set; }

    public string Actor { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;

    public string EntityType { get; set; } = string.Empty;

    public string EntityId { get; set; } = string.Empty;

    public string? Before { get; set; }

    public string? After { get; set; }

    public string RequestId { get; set; } = string.Empty;

    public DateTime OccurredAt { get; set; }
}

public sealed class OutboxMessage : ITenantScoped
{
    // Doubles as event_id in the envelope.
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // The account that owns the workspace (Workspace.OwnerId in Parrot), which
    // catalog.items sends as its tenant_id. Null when the catalog has not yet
    // been told the owner (a token from before the owner_id claim, or old rows).
    public string? OwnerId { get; set; }

    // Who made the change: the sub of the request's token (a user id for a
    // person), "tools" for an operator command. Null on rows from before it was recorded.
    public string? UserId { get; set; }

    public string AggregateType { get; set; } = string.Empty;

    public Guid AggregateId { get; set; }

    public string EventType { get; set; } = string.Empty;

    public int SchemaVersion { get; set; } = 1;

    public string Payload { get; set; } = "{}";

    public DateTime CreatedAt { get; set; }

    public DateTime? PublishedAt { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }
}

/// <summary>Idempotency record for in-repo consumers: (consumer, event_id) is the key.</summary>
public sealed class ConsumerInboxEntry : ITenantScoped
{
    public string Consumer { get; set; } = string.Empty;

    public Guid EventId { get; set; }

    public Guid TenantId { get; set; }

    public DateTime ProcessedAt { get; set; }
}
