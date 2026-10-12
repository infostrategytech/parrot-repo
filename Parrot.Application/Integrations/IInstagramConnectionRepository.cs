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

    // The account the token belongs to, and the token, encrypted; a fresh token, so it counts as just
    // renewed, runs out at expiresAt, and is no longer refused.
    Task SaveAccountAsync(Guid id, string ownerUserId, string accountId, string username, string encryptedAccessToken,
        DateTime expiresAt, CancellationToken cancellationToken = default);

    // Active connections whose token was last renewed (or saved) at or before renewBefore and has not
    // been refused: due for renewal, or past saving if it has already run out.
    Task<IReadOnlyList<InstagramConnection>> GetDueForTokenRenewalAsync(DateTime renewBefore, int take,
        CancellationToken cancellationToken = default);

    // Replaces the token only if it is still the one that was renewed, so a token the owner pasted
    // meanwhile is never overwritten.
    Task<bool> SaveRenewedTokenAsync(Guid id, string previousEncryptedToken, string encryptedAccessToken, DateTime renewedAt,
        DateTime expiresAt, CancellationToken cancellationToken = default);

    // Instagram refused the token: the owner must paste a new one.
    Task MarkTokenRejectedAsync(Guid id, string encryptedAccessToken, DateTime rejectedAt, CancellationToken cancellationToken = default);

    Task SaveAppSecretAsync(Guid id, string ownerUserId, string encryptedSecret, CancellationToken cancellationToken = default);

    // Whether the connection is the owner's, disabled or not: its conversations stay readable.
    Task<bool> IsOwnedByAsync(Guid id, string ownerUserId, CancellationToken cancellationToken = default);

    // Its ?ref= stops routing webhooks at once.
    Task<bool> DisableAsync(Guid id, string ownerUserId, DateTime disabledAt, CancellationToken cancellationToken = default);
}
