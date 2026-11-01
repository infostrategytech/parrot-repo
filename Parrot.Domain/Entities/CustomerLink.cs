namespace Parrot.Domain.Entities;

/// <summary>
/// A reversible link between two customer records that belong to the same person.
/// The primary customer appears in the contacts list and shows all channels from
/// both records. The linked customer is hidden from the list but its identities
/// remain intact so conversations still resolve correctly.
/// </summary>
public class CustomerLink
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string WorkspaceId { get; set; }

    public Guid PrimaryCustomerId { get; set; }

    public Customer PrimaryCustomer { get; set; } = default!;

    /// <summary>The customer record that is hidden in favour of the primary.</summary>
    public Guid LinkedCustomerId { get; set; }

    public Customer LinkedCustomer { get; set; } = default!;

    public DateTime LinkedAt { get; set; } = DateTime.UtcNow;
}
