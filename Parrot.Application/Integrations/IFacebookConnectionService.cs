using Parrot.Application.DTOs.Integrations;

namespace Parrot.Application.Integrations;

// Drives the three-step Messenger setup: issue credentials, verify the webhook, then save the Page.
public interface IFacebookConnectionService
{
    // Returns the webhook URL and verify token to paste into Meta, creating the connection on first call.
    Task<FacebookSetupResponse> GetSetupAsync(string ownerUserId, CancellationToken cancellationToken = default);

    // Handles Meta's hub.challenge: matches the token to a connection and marks it verified.
    Task<bool> VerifyWebhookAsync(string mode, string verifyToken, CancellationToken cancellationToken = default);

    // Stores the Page ID, name and access token once Meta has verified the webhook.
    Task SavePageAsync(string ownerUserId, FacebookPageRequest request, CancellationToken cancellationToken = default);
}
