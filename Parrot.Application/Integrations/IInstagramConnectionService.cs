using Parrot.Application.DTOs.Integrations;

namespace Parrot.Application.Integrations;

// Manual Instagram setup: the business's own Meta app, Instagram API with Instagram Login.
public interface IInstagramConnectionService
{
    // Creates the connection and its verify token on first call; afterwards returns the same ones.
    Task<InstagramSetupResponse> GetSetupAsync(string workspaceId, CancellationToken cancellationToken = default);

    // Meta's GET challenge for ?ref=; marks the connection verified when the token matches.
    Task<bool> VerifyWebhookAsync(string identifier, string mode, string verifyToken, CancellationToken cancellationToken = default);

    // Reads the account from the token, subscribes it to message webhooks, then saves both.
    Task SaveAccountAsync(string workspaceId, InstagramAccountRequest request, CancellationToken cancellationToken = default);

    // The Instagram app secret that signs this connection's webhooks.
    Task SaveAppSecretAsync(string workspaceId, AppSecretRequest request, CancellationToken cancellationToken = default);

    Task DisableAsync(string workspaceId, CancellationToken cancellationToken = default);
}
