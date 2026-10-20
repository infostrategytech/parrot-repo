using Parrot.Domain.Enums;

namespace Parrot.Application.Customers;

public sealed class CustomerService(ICustomerRepository repository)
{
    /// <summary>
    /// Records or refreshes a customer from an inbound message. Safe to call on
    /// every message: a repeat for the same sender updates their last-seen time
    /// and name; it does not create a second customer row.
    /// </summary>
    public Task UpsertFromMessageAsync(
        string ownerUserId,
        MessagingPlatform platform,
        Guid connectionId,
        string sender,
        string? name,
        string? username,
        DateTime seenAt,
        CancellationToken ct) =>
        repository.UpsertAsync(
            ownerUserId,
            connectionId,
            platform.ToString().ToLowerInvariant(),
            sender,
            name is { Length: > 0 } ? name : sender,
            username,
            seenAt,
            ct);
}
