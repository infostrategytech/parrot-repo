using System.Security.Cryptography;
using Parrot.Application.Logging;
using Parrot.Domain.Entities;

namespace Parrot.Application.Integrations;

// Keeps Instagram access tokens alive. A token lasts 60 days and can be renewed once it is a day old,
// each renewal giving another 60; one that lapses can only be replaced by the owner pasting a new
// one. So tokens are renewed when 30 days old, well before they run out, and a token Instagram
// refuses is marked so the Channels card asks for a new one.
public sealed class InstagramTokenRenewer(
    IInstagramConnectionRepository repository,
    IInstagramCredentialProtector protector,
    IInstagramAccountApi instagram,
    IAppLogger<InstagramTokenRenewer> logger)
{
    /// <summary>How old a token is when it is renewed: half its life, so a few failed tries still leave time.</summary>
    public static readonly TimeSpan RenewAfter = TimeSpan.FromDays(30);

    /// <summary>How long a token from the App Dashboard or a renewal lasts.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(60);

    private const int BatchSize = 50;

    public sealed record Result(int Renewed, int Rejected, int Failed);

    public async Task<Result> RenewDueAsync(DateTime now, CancellationToken cancellationToken)
    {
        int renewed = 0, rejected = 0, failed = 0;
        foreach (InstagramConnection connection in await repository.GetDueForTokenRenewalAsync(now - RenewAfter, BatchSize, cancellationToken))
        {
            string encrypted = connection.AccessTokenEncrypted!;

            // Run out already: Instagram cannot renew it, so the owner must paste a new one.
            if (connection.AccessTokenExpiresAt <= now)
            {
                await repository.MarkTokenRejectedAsync(connection.Id, encrypted, now, cancellationToken);
                logger.LogWarning("Instagram token expired before it could be renewed. ConnectionId={ConnectionId}", connection.Id);
                rejected++;
                continue;
            }

            try
            {
                string token = protector.Unprotect(encrypted);
                InstagramRenewedToken fresh = await instagram.RenewTokenAsync(token, cancellationToken);
                bool saved = await repository.SaveRenewedTokenAsync(connection.Id, encrypted, protector.Protect(fresh.AccessToken),
                    now, now + fresh.ExpiresIn, cancellationToken);
                logger.LogInformation("Instagram token renewed. ConnectionId={ConnectionId} Saved={Saved} ExpiresInDays={Days}",
                    connection.Id, saved, (int)fresh.ExpiresIn.TotalDays);
                renewed++;
            }
            catch (InstagramAccountException ex) when (ex.TokenRejected)
            {
                await repository.MarkTokenRejectedAsync(connection.Id, encrypted, now, cancellationToken);
                logger.LogWarning("Instagram refused to renew the token; the owner must paste a new one. ConnectionId={ConnectionId}", connection.Id);
                rejected++;
            }
            catch (CryptographicException)
            {
                // The key ring rotated: the saved token cannot be read, so it cannot be used either.
                await repository.MarkTokenRejectedAsync(connection.Id, encrypted, now, cancellationToken);
                logger.LogWarning("Instagram token could not be decrypted; the owner must paste a new one. ConnectionId={ConnectionId}", connection.Id);
                rejected++;
            }
            catch (Exception ex) when (ex is InstagramAccountException or HttpRequestException or System.Text.Json.JsonException
                                       || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // Instagram unreachable or a passing error: tried again on the next round, long before the token lapses.
                logger.LogWarning(ex, "Instagram token renewal failed; it will be tried again. ConnectionId={ConnectionId}", connection.Id);
                failed++;
            }
        }

        return new Result(renewed, rejected, failed);
    }
}
