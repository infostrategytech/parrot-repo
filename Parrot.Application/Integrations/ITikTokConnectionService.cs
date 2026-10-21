using Parrot.Application.DTOs.Integrations;

namespace Parrot.Application.Integrations;

// Drives the manual TikTok setup: save the customer's own app, then authorize its Business Account.
public interface ITikTokConnectionService
{
    // Returns the current step and the redirect URI to add to the TikTok app. Creates nothing.
    Task<TikTokSetupResponse> GetSetupAsync(string workspaceId, CancellationToken cancellationToken = default);

    // Registers the app's webhook with TikTok, which checks the App ID and secret, then saves the connection.
    Task SaveAppAsync(string workspaceId, TikTokAppRequest request, CancellationToken cancellationToken = default);

    // Builds the TikTok consent URL for the owner's app, with a sealed, short-lived state.
    Task<TikTokAuthorizationResponse> GetAuthorizationAsync(string workspaceId, CancellationToken cancellationToken = default);

    // Finishes the consent TikTok redirected back with: exchanges the code and saves the account and tokens.
    Task CompleteAuthorizationAsync(string? state, string? code, CancellationToken cancellationToken = default);

    // Disconnects the owner's TikTok account: its webhooks are refused from now on.
    Task DisableAsync(string workspaceId, CancellationToken cancellationToken = default);
}
