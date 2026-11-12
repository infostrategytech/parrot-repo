using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Parrot.Application.BusinessSources;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Logging;
using Parrot.Domain.Entities;
using Parrot.Domain.Exceptions;

namespace Parrot.Application.Integrations;

// Setup orchestration for Messenger; the send path and /subscribed_apps call are not wired up yet.
public sealed class FacebookConnectionService(
    IFacebookConnectionRepository repository,
    IFacebookCredentialProtector protector,
    IConnectionSourceSyncService sourceSyncService,
    IAppLogger<FacebookConnectionService> logger,
    IOptions<FacebookSettings> options) : IFacebookConnectionService
{
    // Creates the connection and its verify token on first call; afterwards replays the same credentials.
    public async Task<FacebookSetupResponse> GetSetupAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        // Validated before the insert so a misconfigured server cannot leave orphaned connection rows behind.
        string baseUrl = RequireBaseUrl();

        FacebookConnection? connection = await repository.GetByOwnerAsync(workspaceId, cancellationToken);
        string verifyToken;
        if (connection is null)
        {
            verifyToken = NewCredential();
            connection = new FacebookConnection
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

        return new FacebookSetupResponse(connection.Id,
            $"{baseUrl}/api/webhooks/facebook?ref={connection.WebhookIdentifier}", verifyToken,
            connection.WebhookVerifiedAt.HasValue, connection.PageId, connection.PageName,
            !string.IsNullOrEmpty(connection.AccessTokenEncrypted),
            !string.IsNullOrEmpty(connection.AppSecretEncrypted));
    }

    // Resolves the connection by ?ref= and compares the verify token in fixed time.
    public async Task<bool> VerifyWebhookAsync(string identifier, string mode, string verifyToken,
        CancellationToken cancellationToken = default)
    {
        if (mode != "subscribe" || string.IsNullOrWhiteSpace(identifier) || string.IsNullOrWhiteSpace(verifyToken))
            return false;

        FacebookConnection? connection = await repository.GetActiveByIdentifierAsync(identifier, cancellationToken);
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

        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(verifyToken)))
            return false;

        return await repository.MarkVerifiedAsync(connection.Id, DateTime.UtcNow, cancellationToken);
    }

    // Validates the token shape and encrypts it; rejects Pages saved before webhook verification.
    public async Task SavePageAsync(string workspaceId, FacebookPageRequest request, CancellationToken cancellationToken = default)
    {
        FacebookConnection connection = await repository.GetByOwnerAsync(workspaceId, cancellationToken)
            ?? throw new NotFoundException("Start Facebook webhook setup before saving the Page.");
        if (connection.DisabledAt.HasValue || connection.WebhookVerifiedAt is null)
            throw new ConflictException("Verify the Facebook webhook in Meta before saving the Page.");
        string token = request.AccessToken.Trim();
        if (token.Any(char.IsWhiteSpace) || token.Any(char.IsControl))
            throw new ValidationException("Enter a valid Page access token.");
        await repository.SavePageAsync(connection.Id, workspaceId, request.PageId, request.PageName.Trim(),
            protector.Protect(token), cancellationToken);

        // Best-effort: publish the Facebook page source so the AI knows this workspace is on Messenger.
        try
        {
            await sourceSyncService.SyncFacebookPageAsync(workspaceId, connection.Id, request.PageId, request.PageName.Trim(), cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "facebook.page.source.sync_failed WorkspaceId={WorkspaceId} ConnectionId={ConnectionId}",
                workspaceId, connection.Id);
        }
    }

    public async Task DisableAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        FacebookConnection connection = await repository.GetByOwnerAsync(workspaceId, cancellationToken)
            ?? throw new NotFoundException("There is no Facebook connection to disconnect.");
        await repository.DisableAsync(connection.Id, workspaceId, DateTime.UtcNow, cancellationToken);
    }

    // Manual setup: the customer's own Meta app signs this connection's webhooks with it. Allowed
    // before verification, so it can be saved alongside the callback URL in one sitting.
    public async Task SaveAppSecretAsync(string workspaceId, AppSecretRequest request, CancellationToken cancellationToken = default)
    {
        FacebookConnection connection = await repository.GetByOwnerAsync(workspaceId, cancellationToken)
            ?? throw new NotFoundException("Start Facebook webhook setup before saving the App Secret.");
        if (connection.DisabledAt.HasValue)
            throw new ConflictException("This Facebook connection is disabled.");
        string secret = request.AppSecret?.Trim() ?? string.Empty;
        if (!WebhookSignature.IsAppSecret(secret))
            throw new ValidationException("Enter the App Secret from your Meta app's App settings > Basic: 32 letters and numbers.");
        await repository.SaveAppSecretAsync(connection.Id, workspaceId, protector.Protect(secret), cancellationToken);
    }

    private static string NewCredential() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    // Meta refuses plaintext callbacks, so HTTPS is required here even though WhatsApp also accepts HTTP.
    private string RequireBaseUrl()
    {
        string baseUrl = options.Value.WebhookBaseUrl.TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("Facebook:WebhookBaseUrl must be a public HTTPS URL without query, fragment or credentials.");
        return baseUrl;
    }
}
