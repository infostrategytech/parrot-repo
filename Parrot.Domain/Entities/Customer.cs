namespace Parrot.Domain.Entities;

public class Customer
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string OwnerUserId { get; set; }

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
}
