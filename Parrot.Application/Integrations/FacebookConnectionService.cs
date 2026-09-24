using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Parrot.Application.DTOs.Integrations;
using Parrot.Domain.Entities;
using Parrot.Domain.Exceptions;

namespace Parrot.Application.Integrations;

// Setup orchestration for Messenger; the send path and /subscribed_apps call are not wired up yet.
public sealed class FacebookConnectionService(
    IFacebookConnectionRepository repository,
    IFacebookCredentialProtector protector,
    IOptions<FacebookSettings> options) : IFacebookConnectionService
{
    // Creates the connection and its verify token on first call; afterwards replays the same credentials.
    public async Task<FacebookSetupResponse> GetSetupAsync(string ownerUserId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerUserId);
        FacebookConnection? connection = await repository.GetByOwnerAsync(ownerUserId, cancellationToken);
        string verifyToken;
        if (connection is null)
        {
            verifyToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            connection = new FacebookConnection
            {
                OwnerUserId = ownerUserId,
                VerifyTokenEncrypted = protector.Protect(verifyToken),
            };
            await repository.AddAsync(connection, cancellationToken);
        }
        else
        {
            verifyToken = protector.Unprotect(connection.VerifyTokenEncrypted);
        }

        string baseUrl = options.Value.WebhookBaseUrl.TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("Facebook:WebhookBaseUrl must be a public HTTPS URL without query, fragment or credentials.");

        return new FacebookSetupResponse(connection.Id, $"{baseUrl}/api/webhooks/facebook", verifyToken,
            connection.WebhookVerifiedAt.HasValue, connection.PageId, connection.PageName,
            !string.IsNullOrEmpty(connection.AccessTokenEncrypted));
    }

    // Meta sends no connection reference, so the token is matched against every active connection.
    public async Task<bool> VerifyWebhookAsync(string mode, string verifyToken, CancellationToken cancellationToken = default)
    {
        if (mode != "subscribe" || string.IsNullOrWhiteSpace(verifyToken)) return false;
        // One connection per owner keeps this scan bounded by the number of Parrot customers during manual onboarding.
        // The repository implementation performs the protected-token lookup without exposing tokens through an API.
        if (repository is not IFacebookVerifyTokenLookup lookup) return false;
        FacebookConnection? connection = await lookup.FindByVerifyTokenAsync(verifyToken, protector, cancellationToken);
        return connection is not null && await repository.MarkVerifiedAsync(connection.Id, DateTime.UtcNow, cancellationToken);
    }

    // Validates the token shape and encrypts it; rejects Pages saved before webhook verification.
    public async Task SavePageAsync(string ownerUserId, FacebookPageRequest request, CancellationToken cancellationToken = default)
    {
        FacebookConnection connection = await repository.GetByOwnerAsync(ownerUserId, cancellationToken)
            ?? throw new NotFoundException("Start Facebook webhook setup before saving the Page.");
        if (connection.DisabledAt.HasValue || connection.WebhookVerifiedAt is null)
            throw new ConflictException("Verify the Facebook webhook in Meta before saving the Page.");
        string token = request.AccessToken.Trim();
        if (token.Any(char.IsWhiteSpace) || token.Any(char.IsControl))
            throw new ValidationException("Enter a valid Page access token.");
        await repository.SavePageAsync(connection.Id, ownerUserId, request.PageId, request.PageName.Trim(),
            protector.Protect(token), cancellationToken);
    }
}

// Kept off IFacebookConnectionRepository so decrypt-and-compare stays in the data layer, out of the public API.
public interface IFacebookVerifyTokenLookup
{
    Task<FacebookConnection?> FindByVerifyTokenAsync(string verifyToken, IFacebookCredentialProtector protector,
        CancellationToken cancellationToken = default);
}
