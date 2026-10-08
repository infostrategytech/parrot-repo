using Parrot.Application.DTOs.Integrations;

namespace Parrot.Application.Integrations;

// Drives the three-step Messenger setup: issue credentials, verify the webhook, then save the Page.
public interface IFacebookConnectionService
{
    // Returns the ?ref= callback URL and verify token to paste into Meta, creating the connection on first call.
    Task<FacebookSetupResponse> GetSetupAsync(string ownerUserId, CancellationToken cancellationToken = default);

    // Handles Meta's hub.challenge for the connection named by ?ref=, then marks it verified.
    Task<bool> VerifyWebhookAsync(string identifier, string mode, string verifyToken, CancellationToken cancellationToken = default);

    // Stores the Page ID, name and access token once Meta has verified the webhook.
    Task SavePageAsync(string ownerUserId, FacebookPageRequest request, CancellationToken cancellationToken = default);

    // Disconnects the owner's Page: its webhooks are refused from now on. Setting up again
    // starts a new connection with a new callback URL and verify token.
    Task DisableAsync(string ownerUserId, CancellationToken cancellationToken = default);

    // Manual setup: saves the App Secret the customer's own Meta app signs webhooks with.
    Task SaveAppSecretAsync(string ownerUserId, AppSecretRequest request, CancellationToken cancellationToken = default);
}
