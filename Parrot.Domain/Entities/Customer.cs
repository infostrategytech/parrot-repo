namespace Parrot.Domain.Entities;

public class Customer
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The workspace the customer wrote to. Every lookup is scoped by it.</summary>
    public required string WorkspaceId { get; set; }

    public required string Name { get; set; }

    public string? Email { get; set; }

    /// <summary>Where the customer is in the funnel: Customer, New Lead, Interested or Vip.</summary>
    public string Stage { get; set; } = "Customer";

    /// <summary>Free-form labels stored comma-separated and exposed as a list.</summary>
    public List<string> Tags { get; set; } = [];

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }

    /// <summary>When the last inbound message from this customer arrived.</summary>
    public DateTime? LastSeenAt { get; set; }

    /// <summary>Channel of that last message: whatsapp, instagram, facebook or tiktok.</summary>
    public string? LastSeenChannel { get; set; }

    public List<CustomerIdentity> Identities { get; set; } = [];

    /// <summary>Phone number stored at contact creation time, before any WhatsApp message arrives.</summary>
    public string? Phone { get; set; }

    /// <summary>Platform of the manually specified identifier: whatsapp, instagram, facebook or tiktok.</summary>
    public string? ChannelPlatform { get; set; }

    /// <summary>Phone number or @handle stored at creation time as free text; matched when a real message arrives.</summary>
    public string? ChannelIdentifier { get; set; }
}
