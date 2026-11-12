using Parrot.Domain.Enums;

namespace Parrot.Domain.Entities;

/// <summary>
/// One independently identifiable business resource on a connected channel — a WhatsApp
/// Business Profile, a WhatsApp Catalog, an Instagram profile, or a Facebook Page.
/// Each source belongs to one workspace, is authorised through one connection (by Channel),
/// and carries lightweight metadata fetched in the background.
/// </summary>
public class BusinessSource
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>string — matches WorkspaceId type on all connection entities.</summary>
    public required string WorkspaceId { get; set; }

    public Channel Channel { get; set; }
    public BusinessSourceKind Kind { get; set; }

    /// <summary>
    /// The Id of the connection entity that provides authorisation for this source.
    /// This is a soft reference — no FK constraint, because connection entities share no base table.
    /// Use <see cref="Channel"/> to determine which connection repository to query.
    /// </summary>
    public Guid AuthorizationConnectionId { get; set; }

    /// <summary>
    /// The stable provider-side identifier. Format per channel:
    ///   WhatsApp profile  → WhatsAppConnection.PhoneNumberId
    ///   WhatsApp catalog  → Meta catalogue ID
    ///   Instagram         → InstagramConnection.AccountId
    ///   Facebook          → FacebookConnection.PageId
    /// </summary>
    public required string ExternalId { get; set; }

    public string? DisplayName { get; set; }

    /// <summary>Username or handle (e.g. Instagram username, Facebook page username). Not a phone number.</summary>
    public string? Handle { get; set; }

    public string? Url { get; set; }

    /// <summary>
    /// Available profile metadata as a JSON object; missing fields are omitted.
    /// Exact structure agreed with Omni (open item O4).
    /// </summary>
    public string? Text { get; set; }

    /// <summary>When Parrot last successfully retrieved the metadata stored in Text.</summary>
    public DateTime? CapturedAt { get; set; }

    // ── Metadata sync lifecycle ──────────────────────────────────────────────

    public MetadataSyncStatus MetadataSyncStatus { get; set; } = MetadataSyncStatus.Pending;

    public DateTime? LastMetadataSyncAttemptAt { get; set; }

    public DateTime? NextMetadataSyncAttemptAt { get; set; }

    public DateTime? LastMetadataSyncSucceededAt { get; set; }

    public string? LastMetadataSyncError { get; set; }

    /// <summary>
    /// Set by the metadata worker when it claims this source. Prevents two workers
    /// from processing the same source simultaneously. Clears on success or when the
    /// lease expires (another worker may then reclaim it).
    /// </summary>
    public DateTime? MetadataSyncLockedUntil { get; set; }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
