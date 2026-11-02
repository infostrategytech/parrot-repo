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
        IReadOnlySet<Guid>? connections,
        CancellationToken ct);

    Task<Customer?> FindAsync(string ownerUserId, Guid id, CancellationToken ct);

    Task<Customer> CreateAsync(Customer customer, CancellationToken ct);

    Task<Customer?> UpdateStageAsync(string ownerUserId, Guid id, string stage, CancellationToken ct);

    Task<Customer?> UpdateTagsAsync(string ownerUserId, Guid id, List<string> tags, CancellationToken ct);

    Task<Customer?> UpdateChannelIdentifierAsync(string ownerUserId, Guid id, string? channelPlatform, string? channelIdentifier, CancellationToken ct);

    /// <summary>
    /// Returns the primary customer for <paramref name="id"/>. If <paramref name="id"/> is a linked
    /// (non-primary) customer, the primary is returned instead. Returns null when neither is found
    /// or neither belongs to <paramref name="ownerUserId"/>.
    /// </summary>
    Task<(Customer Primary, List<Domain.Entities.CustomerLink> Links)?> GetLinksAsync(string ownerUserId, Guid id, CancellationToken ct);

    /// <summary>
    /// Finds the primary customer whose identity matches <paramref name="connectionId"/> and
    /// <paramref name="sender"/>. If that customer is itself linked to a primary, returns the primary.
    /// Returns null when not found or not owned by <paramref name="ownerUserId"/>.
    /// </summary>
    Task<(Customer Primary, List<Domain.Entities.CustomerLink> Links)?> FindByIdentityAsync(string ownerUserId, Guid connectionId, string sender, CancellationToken ct);

    /// <summary>
    /// Links <paramref name="linkedId"/> under <paramref name="primaryId"/>. Returns the updated
    /// primary with its links. Throws <see cref="Domain.Exceptions.ConflictException"/> when
    /// the constraint would be violated.
    /// </summary>
    Task<(Customer Primary, List<Domain.Entities.CustomerLink> Links)> LinkAsync(string ownerUserId, Guid primaryId, Guid linkedId, CancellationToken ct);

    /// <summary>
    /// Removes the link identified by <paramref name="linkId"/> from <paramref name="primaryId"/>.
    /// Returns the updated primary. Throws <see cref="Domain.Exceptions.NotFoundException"/> when
    /// the link or either customer is not found.
    /// </summary>
    Task<(Customer Primary, List<Domain.Entities.CustomerLink> Links)> UnlinkAsync(string ownerUserId, Guid primaryId, Guid linkId, CancellationToken ct);
}
