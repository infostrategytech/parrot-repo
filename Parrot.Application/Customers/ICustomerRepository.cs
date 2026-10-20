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
}
