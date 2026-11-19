using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Parrot.Application.BusinessSources;
using Parrot.Application.Contracts.BusinessSources;
using Parrot.Application.Integrations;
using Parrot.Application.Logging;
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
    IOptions<FacebookSettings> facebookSettings,
    IAppLogger<BusinessSourceCredentialsService> logger) : IBusinessSourceCredentialsService
{
    public async Task<BusinessSourceCredentialsResponse> GetCredentialsAsync(
        Guid sourceId, string workspaceId, CancellationToken ct = default)
    {
        BusinessSource? source = await db.BusinessSources
            .Where(s => s.Id == sourceId && s.WorkspaceId == workspaceId && s.IsActive)
            .FirstOrDefaultAsync(ct);
        if (source is null)
        {
            await LogMissingSourceAsync(sourceId, workspaceId, ct);
            throw new NotFoundException("Business source not found.");
        }

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
        // The connection must exist (and not be disabled) before its token is read: a live
        // connection whose token has not been saved yet is "no token", which is a conflict the
        // caller should resolve by waiting for setup — not the same answer as a missing source.
        if (!await db.WhatsAppConnections.AnyAsync(
                c => c.Id == source.AuthorizationConnectionId && c.WorkspaceId == source.WorkspaceId && c.DisabledAt == null,
                ct))
        {
            throw Refused(source, "the source's connection is missing or disabled");
        }

        string? encryptedToken = await db.WhatsAppConnections
            .Where(c => c.Id == source.AuthorizationConnectionId
                     && c.WorkspaceId == source.WorkspaceId
                     && c.DisabledAt == null)
            .Select(c => c.AccessTokenEncrypted)
            .FirstOrDefaultAsync(ct);

        return BuildResponse(source, encryptedToken, whatsAppProtector.Unprotect, whatsAppSettings.Value.ApiVersion);
    }

    private async Task<BusinessSourceCredentialsResponse> InstagramCredentialsAsync(BusinessSource source, CancellationToken ct)
    {
        if (!await db.InstagramConnections.AnyAsync(
                c => c.Id == source.AuthorizationConnectionId && c.WorkspaceId == source.WorkspaceId && c.DisabledAt == null,
                ct))
        {
            throw Refused(source, "the source's connection is missing or disabled");
        }

        string? encryptedToken = await db.InstagramConnections
            .Where(c => c.Id == source.AuthorizationConnectionId
                     && c.WorkspaceId == source.WorkspaceId
                     && c.DisabledAt == null)
            .Select(c => c.AccessTokenEncrypted)
            .FirstOrDefaultAsync(ct);

        return BuildResponse(source, encryptedToken, instagramProtector.Unprotect, instagramSettings.Value.ApiVersion);
    }

    private async Task<BusinessSourceCredentialsResponse> FacebookCredentialsAsync(BusinessSource source, CancellationToken ct)
    {
        if (!await db.FacebookConnections.AnyAsync(
                c => c.Id == source.AuthorizationConnectionId && c.WorkspaceId == source.WorkspaceId && c.DisabledAt == null,
                ct))
        {
            throw Refused(source, "the source's connection is missing or disabled");
        }

        string? encryptedToken = await db.FacebookConnections
            .Where(c => c.Id == source.AuthorizationConnectionId
                     && c.WorkspaceId == source.WorkspaceId
                     && c.DisabledAt == null)
            .Select(c => c.AccessTokenEncrypted)
            .FirstOrDefaultAsync(ct);

        return BuildResponse(source, encryptedToken, facebookProtector.Unprotect, facebookSettings.Value.ApiVersion);
    }

    // Callers resolve the connection first (missing or disabled → 404 upstream, so the source
    // id cannot be probed). Here a token that is null or empty therefore means the connection
    // exists but the token has not been saved yet — 409 so the caller knows to wait for setup.
    private BusinessSourceCredentialsResponse BuildResponse(
        BusinessSource source, string? encryptedToken, Func<string, string> unprotect, string apiVersion)
    {
        if (string.IsNullOrEmpty(encryptedToken))
        {
            LogRefused(source.Id, source.WorkspaceId, source.WorkspaceId, source.Channel, "the connection has no access token saved yet (409)");
            throw new ConflictException("This connection has no access token configured yet.");
        }
        return new BusinessSourceCredentialsResponse(unprotect(encryptedToken!), apiVersion);
    }

    // The caller always hears the same "not found"; the log says which it was, so a wrong tenant_id on
    // Omni's token (the source is in another workspace) is easy to tell from a source that never existed.
    private async Task LogMissingSourceAsync(Guid sourceId, string workspaceId, CancellationToken ct)
    {
        BusinessSource? any = await db.BusinessSources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        string reason = any is null ? "no such source"
            : any.WorkspaceId != workspaceId ? "the source belongs to another workspace than the token's tenant_id"
            : "the source is inactive (its channel was disconnected)";
        LogRefused(sourceId, workspaceId, any?.WorkspaceId, any?.Channel, reason);
    }

    private NotFoundException Refused(BusinessSource source, string reason)
    {
        LogRefused(source.Id, source.WorkspaceId, source.WorkspaceId, source.Channel, reason);
        return new NotFoundException("Business source not found.");
    }

    private void LogRefused(Guid sourceId, string workspaceId, string? sourceWorkspaceId, Channel? channel, string reason) =>
        logger.LogWarning(
            "internal.credentials.refused SourceId={SourceId} WorkspaceId={WorkspaceId} SourceWorkspaceId={SourceWorkspaceId} Channel={Channel} Reason={Reason}",
            sourceId, workspaceId, sourceWorkspaceId, channel, reason);
}
