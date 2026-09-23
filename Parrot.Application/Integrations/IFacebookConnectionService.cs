using Parrot.Application.DTOs.Integrations;

namespace Parrot.Application.Integrations;

public interface IFacebookConnectionService
{
    Task<FacebookSetupResponse> GetSetupAsync(string ownerUserId, CancellationToken cancellationToken = default);
    Task<bool> VerifyWebhookAsync(string mode, string verifyToken, CancellationToken cancellationToken = default);
    Task SavePageAsync(string ownerUserId, FacebookPageRequest request, CancellationToken cancellationToken = default);
}
