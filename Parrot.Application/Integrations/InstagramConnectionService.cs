using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Parrot.Application.BusinessSources;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Logging;
using Parrot.Domain.Entities;
using Parrot.Domain.Exceptions;

namespace Parrot.Application.Integrations;

// Manual setup with the business's own Meta app (Instagram API with Instagram Login), in the order the
// Channels screen walks it: callback URL and verify token into Meta, then the access token and the
// Instagram app secret into Parrot.
public sealed class InstagramConnectionService(
    IInstagramConnectionRepository repository,
    IInstagramCredentialProtector protector,
    IInstagramAccountApi instagram,
    IConnectionSourceSyncService sourceSyncService,
    IAppLogger<InstagramConnectionService> logger,
    IOptions<InstagramSettings> options) : IInstagramConnectionService
{
    public async Task<InstagramSetupResponse> GetSetupAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        // Validated before the insert so a misconfigured server cannot leave orphaned connection rows behind.
        string baseUrl = RequireBaseUrl();

        InstagramConnection? connection = await repository.GetByOwnerAsync(workspaceId, cancellationToken);
        string verifyToken;
        if (connection is null)
        {
            verifyToken = NewCredential();
            connection = new InstagramConnection
            {
                WorkspaceId = workspaceId,
                VerifyTokenEncrypted = protector.Protect(verifyToken),
            };
            await repository.AddAsync(connection, cancellationToken);
        }
        else
        {
            verifyToken = protector.Unprotect(connection.VerifyTokenEncrypted);
        }

        return new InstagramSetupResponse(connection.Id,
            $"{baseUrl}/api/webhooks/instagram?ref={connection.WebhookIdentifier}", verifyToken,
            connection.WebhookVerifiedAt.HasValue, connection.AccountId, connection.Username,
            !string.IsNullOrEmpty(connection.AccessTokenEncrypted),
            !string.IsNullOrEmpty(connection.AppSecretEncrypted),
            connection.AccessTokenExpiresAt,
            NeedsNewToken: !string.IsNullOrEmpty(connection.AccessTokenEncrypted)
                && (connection.AccessTokenRejectedAt is not null || connection.AccessTokenExpiresAt <= DateTime.UtcNow));
    }

    public async Task<bool> VerifyWebhookAsync(string identifier, string mode, string verifyToken,
        CancellationToken cancellationToken = default)
    {
        if (mode != "subscribe" || string.IsNullOrWhiteSpace(identifier) || string.IsNullOrWhiteSpace(verifyToken))
            return false;

        InstagramConnection? connection = await repository.GetActiveByIdentifierAsync(identifier, cancellationToken);
        if (connection is null) return false;

        string expected;
        try
        {
            expected = protector.Unprotect(connection.VerifyTokenEncrypted);
        }
        catch (CryptographicException)
        {
            return false;
        }

        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(verifyToken)))
            return false;

        return await repository.MarkVerifiedAsync(connection.Id, DateTime.UtcNow, cancellationToken);
    }

    // The account is read from the token rather than typed in: webhooks carry the professional
    // account's user_id, which differs from the id a token exchange shows, and a typed id that is off
    // by a digit would silently refuse every message. Subscribing here is the step businesses most
    // often miss; without it Meta sends nothing. Nothing is saved unless both calls succeed.
    public async Task SaveAccountAsync(string workspaceId, InstagramAccountRequest request, CancellationToken cancellationToken = default)
    {
        InstagramConnection connection = await repository.GetByOwnerAsync(workspaceId, cancellationToken)
            ?? throw new NotFoundException("Start Instagram webhook setup before saving the account.");
        if (connection.DisabledAt.HasValue || connection.WebhookVerifiedAt is null)
            throw new ConflictException("Verify the Instagram webhook in Meta before saving the account.");
        string token = request.AccessToken?.Trim() ?? string.Empty;
        if (token.Length is 0 or > 8192 || token.Any(char.IsWhiteSpace) || token.Any(char.IsControl))
            throw new ValidationException("Enter a valid Instagram access token.");

        InstagramAccount account;
        try
        {
            account = await instagram.GetAccountAsync(token, cancellationToken);
            await instagram.SubscribeToMessagesAsync(token, cancellationToken);
        }
        catch (InstagramAccountException ex)
        {
            throw new ValidationException(ex.Message);
        }

        // A token from the App Dashboard lasts 60 days; the renewer keeps it going from here.
        await repository.SaveAccountAsync(connection.Id, workspaceId, account.AccountId, account.Username,
            protector.Protect(token), DateTime.UtcNow + InstagramTokenRenewer.Lifetime, cancellationToken);

        // Best-effort: publish the Instagram profile source so the AI knows this workspace is on Instagram.
        try
        {
            await sourceSyncService.SyncInstagramProfileAsync(workspaceId, connection.Id, account.AccountId, account.Username, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "instagram.profile.source.sync_failed WorkspaceId={WorkspaceId} ConnectionId={ConnectionId}",
                workspaceId, connection.Id);
        }
    }

    // Allowed before verification, so it can be saved alongside the callback URL in one sitting.
    public async Task SaveAppSecretAsync(string workspaceId, AppSecretRequest request, CancellationToken cancellationToken = default)
    {
        InstagramConnection connection = await repository.GetByOwnerAsync(workspaceId, cancellationToken)
            ?? throw new NotFoundException("Start Instagram webhook setup before saving the app secret.");
        if (connection.DisabledAt.HasValue)
            throw new ConflictException("This Instagram connection is disabled.");
        string secret = request.AppSecret?.Trim() ?? string.Empty;
        if (!WebhookSignature.IsAppSecret(secret))
            throw new ValidationException(
                "Enter the Instagram app secret from Instagram > API setup with Instagram login in your Meta app: 32 letters and numbers. It is not the App Secret under App settings > Basic.");
        await repository.SaveAppSecretAsync(connection.Id, workspaceId, protector.Protect(secret), cancellationToken);
    }

    public async Task DisableAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        InstagramConnection connection = await repository.GetByOwnerAsync(workspaceId, cancellationToken)
            ?? throw new NotFoundException("There is no Instagram connection to disconnect.");
        await repository.DisableAsync(connection.Id, workspaceId, DateTime.UtcNow, cancellationToken);
    }

    private static string NewCredential() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    // Meta refuses plaintext callbacks.
    private string RequireBaseUrl()
    {
        string baseUrl = options.Value.WebhookBaseUrl.TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("Instagram:WebhookBaseUrl must be a public HTTPS URL without query, fragment or credentials.");
        return baseUrl;
    }
}
