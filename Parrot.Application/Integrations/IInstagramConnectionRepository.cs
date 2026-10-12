using Parrot.Domain.Entities;

namespace Parrot.Application.Integrations;

public interface IInstagramConnectionRepository
{
    // The owner's active connection, or null.
    Task<InstagramConnection?> GetByOwnerAsync(string ownerUserId, CancellationToken cancellationToken = default);

    // Webhook routing by ?ref=; only active connections.
    Task<InstagramConnection?> GetActiveByIdentifierAsync(string identifier, CancellationToken cancellationToken = default);

    Task AddAsync(InstagramConnection connection, CancellationToken cancellationToken = default);

    // Records the first verification only, so Meta's repeat challenges are harmless.
    Task<bool> MarkVerifiedAsync(Guid id, DateTime verifiedAt, CancellationToken cancellationToken = default);

    // The account the token belongs to, and the token, encrypted.
    Task SaveAccountAsync(Guid id, string ownerUserId, string accountId, string username, string encryptedAccessToken,
        CancellationToken cancellationToken = default);

    Task SaveAppSecretAsync(Guid id, string ownerUserId, string encryptedSecret, CancellationToken cancellationToken = default);

    // Whether the connection is the owner's, disabled or not: its conversations stay readable.
    Task<bool> IsOwnedByAsync(Guid id, string ownerUserId, CancellationToken cancellationToken = default);

    // Its ?ref= stops routing webhooks at once.
    Task<bool> DisableAsync(Guid id, string ownerUserId, DateTime disabledAt, CancellationToken cancellationToken = default);
}
