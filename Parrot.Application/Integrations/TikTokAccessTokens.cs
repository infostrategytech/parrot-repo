using System.Security.Cryptography;
using Parrot.Domain.Entities;
using Parrot.Domain.Exceptions;

namespace Parrot.Application.Integrations;

// Gives a send a TikTok access token that is still good, renewing it with the refresh token when it is about to
// lapse (they last about a day). When the refresh token has lapsed too, the owner has to authorize again.
public sealed class TikTokAccessTokens(
    ITikTokConnectionRepository repository,
    ITikTokCredentialProtector protector,
    ITikTokApiClient tikTok,
    TimeProvider time)
{
    // Renewed this early so a token does not lapse between being handed out and TikTok receiving the send.
    private static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(5);

    private const string Reauthorize = "TikTok access has expired. Authorize TikTok again in the channel's setup.";

    public async Task<string> GetAsync(TikTokConnection connection, CancellationToken cancellationToken)
    {
        if (connection.AccessTokenEncrypted is null || connection.AccessTokenExpiresAt is null
            || connection.RefreshTokenEncrypted is null || connection.RefreshTokenExpiresAt is null)
            throw new ConflictException("Authorize TikTok in the channel's setup before sending.");

        DateTime now = time.GetUtcNow().UtcDateTime;
        if (connection.AccessTokenExpiresAt.Value > now + RenewBefore) return Read(connection.AccessTokenEncrypted);
        if (connection.RefreshTokenExpiresAt.Value <= now) throw new ConflictException(Reauthorize);

        TikTokRefreshedTokens refreshed;
        try
        {
            refreshed = await tikTok.RefreshTokensAsync(connection.AppId, Read(connection.AppSecretEncrypted),
                Read(connection.RefreshTokenEncrypted), cancellationToken);
        }
        catch (TikTokApiException)
        {
            // Refused: most likely another send just used this refresh token and rotated it.
            return await StoredAsync(connection, cancellationToken) ?? throw new ConflictException(Reauthorize);
        }
        catch (Exception ex) when (ex is HttpRequestException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new ServiceUnavailableException("TikTok could not be reached. Try again in a moment.", ex);
        }

        now = time.GetUtcNow().UtcDateTime;
        bool saved = await repository.SaveRefreshedTokensAsync(connection.Id, connection.RefreshTokenEncrypted,
            protector.Protect(refreshed.AccessToken), now + refreshed.AccessTokenLifetime,
            protector.Protect(refreshed.RefreshToken), now + refreshed.RefreshTokenLifetime, cancellationToken);

        // Not saved means another send renewed first; its tokens are the ones on record.
        return saved ? refreshed.AccessToken : await StoredAsync(connection, cancellationToken) ?? refreshed.AccessToken;
    }

    // The access token now on record, if it is good.
    private async Task<string?> StoredAsync(TikTokConnection connection, CancellationToken cancellationToken)
    {
        TikTokConnection? latest = await repository.GetByOwnerAsync(connection.OwnerUserId, cancellationToken);
        return latest is { AccessTokenEncrypted: not null, AccessTokenExpiresAt: { } expires }
            && latest.Id == connection.Id && expires > time.GetUtcNow().UtcDateTime
                ? Read(latest.AccessTokenEncrypted)
                : null;
    }

    private string Read(string encrypted)
    {
        try
        {
            return protector.Unprotect(encrypted);
        }
        catch (CryptographicException)
        {
            throw new ConflictException("The saved TikTok credentials cannot be read. Authorize TikTok again in the channel's setup.");
        }
    }
}
