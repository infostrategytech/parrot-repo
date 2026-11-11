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

/// <summary>
/// Where the AI team's processing of one item stands, so the business can see that a
/// save reached the catalog but the AI is still catching up. One row per product.
///
/// The catalog sets <see cref="SentEventId"/> to the id of each item message it sends;
/// the AI team's report names the message it processed. The two matching means the AI
/// has caught up with the latest save. Kept apart from products so the AI's reports
/// never write to the rows a business edits.
/// </summary>
public sealed class ItemAiStatus : ITenantScoped
{
    public Guid ProductId { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The latest item message sent for this product (its event_id).</summary>
    public Guid? SentEventId { get; set; }

    /// <summary>ItemChanged or ItemRemoved.</summary>
    public string? SentType { get; set; }

    public DateTime? SentAt { get; set; }

    /// <summary>The item message the AI's latest applied report was about.</summary>
    public Guid? ReportedEventId { get; set; }

    /// <summary>The report's own event_id.</summary>
    public Guid? ReportId { get; set; }

    public DateTime? ReportedAt { get; set; }

    /// <summary>The report's overall status, as the AI sent it (indexed, failed, removed…).</summary>
    public string? Status { get; set; }

    /// <summary>The report's text status (embedded, failed…).</summary>
    public string? TextStatus { get; set; }

    /// <summary>
    /// The <c>version</c> sent with the latest item message. It goes up on every message,
    /// because the AI team orders a product's messages by it; product.Version only goes up
    /// when a published product is snapshotted.
    /// </summary>
    public long SentVersion { get; set; }

    /// <summary>ItemIndexed, ItemUnindexed or ItemIndexFailed.</summary>
    public string? ReportType { get; set; }

    /// <summary>Whether the AI's agents can find this version of the item.</summary>
    public bool? Searchable { get; set; }

    /// <summary>client_err (the business should fix the item) or server_err (the AI's side); set when the text failed.</summary>
    public string? TextReasonCode { get; set; }

    public string? TextReason { get; set; }

    /// <summary>completed, failed, skipped, removed…</summary>
    public string? PhotosStatus { get; set; }

    public int PhotosRejected { get; set; }

    public int PhotosFailed { get; set; }

    /// <summary>The report's per-photo results, kept as sent (JSON array).</summary>
    public string? Photos { get; set; }
}
