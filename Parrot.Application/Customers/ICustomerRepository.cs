using Parrot.Domain.Entities;

namespace Parrot.Application.Customers;

public interface ICustomerRepository
{
    /// <summary>
    /// Creates or updates the customer for this channel identity. Idempotent:
    /// calling it again for the same (connectionId, sender) pair updates
    /// LastSeenAt, Name and Username only — it does not create a second row.
    /// The unique index on (ConnectionId, Sender) is the final deduplication
    /// guard if two deliveries race.
    /// </summary>
    Task UpsertAsync(
        string ownerUserId,
        Guid connectionId,
        string platform,
        string sender,
        string name,
        string? username,
        DateTime seenAt,
        CancellationToken ct);

    Task<(List<Customer> Items, int Total)> ListAsync(
        string ownerUserId,
        string? search,
        string? stage,
        string? channel,
        int skip,
        int take,
        CancellationToken ct);

    Task<Customer?> FindAsync(string ownerUserId, Guid id, CancellationToken ct);

    Task<Customer> CreateAsync(Customer customer, CancellationToken ct);
}
