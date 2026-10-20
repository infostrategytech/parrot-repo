namespace Parrot.Domain.Entities;

/// <summary>
/// One channel identity for a customer. A customer may have one per channel
/// (WhatsApp, Messenger, Instagram, TikTok). The unique index on
/// (ConnectionId, Sender) is the deduplication guard at the database level.
/// </summary>
public class CustomerIdentity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CustomerId { get; set; }

    public Customer Customer { get; set; } = default!;

    /// <summary>The connection through which this identity was first seen.</summary>
    public Guid ConnectionId { get; set; }

    /// <summary>whatsapp, instagram, facebook or tiktok.</summary>
    public required string Platform { get; set; }

    /// <summary>
    /// The channel-native sender id: phone number (WhatsApp), PSID (Messenger),
    /// IGSID (Instagram), open id (TikTok).
    /// </summary>
    public required string Sender { get; set; }

    /// <summary>Handle without the @, when the channel gave one (Instagram, TikTok).</summary>
    public string? Username { get; set; }

    public DateTime LinkedAt { get; set; } = DateTime.UtcNow;
}
