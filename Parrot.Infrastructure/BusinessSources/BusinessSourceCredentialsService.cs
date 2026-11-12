using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Parrot.Application.BusinessSources;
using Parrot.Application.Contracts.BusinessSources;
using Parrot.Application.Integrations;
using Parrot.Domain.Entities;
using Parrot.Domain.Enums;
using Parrot.Domain.Exceptions;
using Parrot.Infrastructure.Data;

namespace Parrot.Infrastructure.BusinessSources;

/// <summary>
/// Returns decrypted credentials for a business source, scoped to the calling workspace.
/// The connection lookup is always scoped to the same workspace as the source, so a source ID
/// from workspace A cannot expose a connection from workspace B.
/// </summary>
public sealed class BusinessSourceCredentialsService(
    ApplicationDbContext db,
    IWhatsAppTokenProtector whatsAppProtector,
    IInstagramCredentialProtector instagramProtector,
    IFacebookCredentialProtector facebookProtector,
    IOptions<WhatsAppSettings> whatsAppSettings,
    IOptions<InstagramSettings> instagramSettings,
    IOptions<FacebookSettings> facebookSettings) : IBusinessSourceCredentialsService
{
    public async Task<BusinessSourceCredentialsResponse> GetCredentialsAsync(
        Guid sourceId, string workspaceId, CancellationToken ct = default)
    {
        BusinessSource? source = await db.BusinessSources
            .Where(s => s.Id == sourceId && s.WorkspaceId == workspaceId && s.IsActive)
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Business source not found.");

        return source.Channel switch
        {
            Channel.WhatsApp => await WhatsAppCredentialsAsync(source, ct),
            Channel.Instagram => await InstagramCredentialsAsync(source, ct),
            Channel.Facebook => await FacebookCredentialsAsync(source, ct),
            _ => throw new InvalidOperationException($"Unsupported channel {source.Channel}.")
        };
    }

    private async Task<BusinessSourceCredentialsResponse> WhatsAppCredentialsAsync(BusinessSource source, CancellationToken ct)
    {
        string? encryptedToken = await db.WhatsAppConnections
            .Where(c => c.Id == source.AuthorizationConnectionId
                     && c.WorkspaceId == source.WorkspaceId
                     && c.DisabledAt == null)
            .Select(c => c.AccessTokenEncrypted)
            .FirstOrDefaultAsync(ct);

        return BuildResponse(encryptedToken, whatsAppProtector.Unprotect, whatsAppSettings.Value.ApiVersion);
    }

    private async Task<BusinessSourceCredentialsResponse> InstagramCredentialsAsync(BusinessSource source, CancellationToken ct)
    {
        string? encryptedToken = await db.InstagramConnections
            .Where(c => c.Id == source.AuthorizationConnectionId
                     && c.WorkspaceId == source.WorkspaceId
                     && c.DisabledAt == null)
            .Select(c => c.AccessTokenEncrypted)
            .FirstOrDefaultAsync(ct);

        return BuildResponse(encryptedToken, instagramProtector.Unprotect, instagramSettings.Value.ApiVersion);
    }

    private async Task<BusinessSourceCredentialsResponse> FacebookCredentialsAsync(BusinessSource source, CancellationToken ct)
    {
        string? encryptedToken = await db.FacebookConnections
            .Where(c => c.Id == source.AuthorizationConnectionId
                     && c.WorkspaceId == source.WorkspaceId
                     && c.DisabledAt == null)
            .Select(c => c.AccessTokenEncrypted)
            .FirstOrDefaultAsync(ct);

        return BuildResponse(encryptedToken, facebookProtector.Unprotect, facebookSettings.Value.ApiVersion);
    }

    // null = connection not found (or disabled) → same 404 as unknown source (no existence leak).
    // empty = connection exists but no token yet → 409 so the caller knows to wait for setup.
    private static BusinessSourceCredentialsResponse BuildResponse(
        string? encryptedToken, Func<string, string> unprotect, string apiVersion)
    {
        if (encryptedToken is null)
            throw new NotFoundException("Business source not found.");
        if (string.IsNullOrEmpty(encryptedToken))
            throw new ConflictException("This connection has no access token configured yet.");
        return new BusinessSourceCredentialsResponse(unprotect(encryptedToken), apiVersion);
    }
}
