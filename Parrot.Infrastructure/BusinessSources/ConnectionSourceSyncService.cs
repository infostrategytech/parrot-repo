using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Parrot.Application.BusinessSources;
using Parrot.Application.Logging;
using Parrot.Domain.Entities;
using Parrot.Domain.Enums;
using Parrot.Infrastructure.Data;

namespace Parrot.Infrastructure.BusinessSources;

public sealed class ConnectionSourceSyncService : IConnectionSourceSyncService
{
    private readonly ApplicationDbContext _db;
    private readonly IBusinessSourceRepository _sources;
    private readonly ISnapshotPublisher _publisher;
    private readonly IAppLogger<ConnectionSourceSyncService> _logger;

    public ConnectionSourceSyncService(
        ApplicationDbContext db,
        IBusinessSourceRepository sources,
        ISnapshotPublisher publisher,
        IAppLogger<ConnectionSourceSyncService> logger)
    {
        _db = db;
        _sources = sources;
        _publisher = publisher;
        _logger = logger;
    }

    public async Task SyncWhatsAppProfileAsync(string workspaceId, Guid connectionId, string phoneNumberId, CancellationToken ct = default)
    {
        await using IDbContextTransaction tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            BusinessSource source = new BusinessSource
            {
                WorkspaceId = workspaceId,
                AuthorizationConnectionId = connectionId,
                Channel = Channel.WhatsApp,
                Kind = BusinessSourceKind.Profile,
                ExternalId = phoneNumberId,
                IsActive = true,
                MetadataSyncStatus = MetadataSyncStatus.Pending,
            };
            await _sources.UpsertAsync(source, ct);
            await _db.SaveChangesAsync(ct);
            await _publisher.PublishAsync(workspaceId, ct);
            await tx.CommitAsync(ct);

            _logger.LogInformation("whatsapp.profile.source.synced WorkspaceId={WorkspaceId} ConnectionId={ConnectionId} PhoneNumberId={PhoneNumberId}",
                workspaceId, connectionId, phoneNumberId);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task SyncWhatsAppCatalogAsync(string workspaceId, Guid connectionId, string? catalogId, CancellationToken ct = default)
    {
        await using IDbContextTransaction tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            if (catalogId is not null)
            {
                // Catalog content is managed by the catalogue sync service, not the metadata worker.
                BusinessSource source = new BusinessSource
                {
                    WorkspaceId = workspaceId,
                    AuthorizationConnectionId = connectionId,
                    Channel = Channel.WhatsApp,
                    Kind = BusinessSourceKind.Catalog,
                    ExternalId = catalogId,
                    IsActive = true,
                    MetadataSyncStatus = MetadataSyncStatus.Unavailable,
                };
                await _sources.UpsertAsync(source, ct);
                await _db.SaveChangesAsync(ct);
            }
            else
            {
                // No catalog on this WABA — deactivate any existing catalog source for this connection.
                await _db.BusinessSources
                    .Where(s => s.WorkspaceId == workspaceId
                                && s.AuthorizationConnectionId == connectionId
                                && s.Channel == Channel.WhatsApp
                                && s.Kind == BusinessSourceKind.Catalog
                                && s.IsActive)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(s => s.IsActive, false)
                        .SetProperty(s => s.UpdatedAt, DateTime.UtcNow), ct);
            }

            await _publisher.PublishAsync(workspaceId, ct);
            await tx.CommitAsync(ct);

            _logger.LogInformation("whatsapp.catalog.source.synced WorkspaceId={WorkspaceId} ConnectionId={ConnectionId} CatalogId={CatalogId}",
                workspaceId, connectionId, catalogId);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task SyncInstagramProfileAsync(string workspaceId, Guid connectionId, string accountId, string username, CancellationToken ct = default)
    {
        await using IDbContextTransaction tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            BusinessSource source = new BusinessSource
            {
                WorkspaceId = workspaceId,
                AuthorizationConnectionId = connectionId,
                Channel = Channel.Instagram,
                Kind = BusinessSourceKind.Profile,
                ExternalId = accountId,
                Handle = username,
                IsActive = true,
                MetadataSyncStatus = MetadataSyncStatus.Pending,
            };
            await _sources.UpsertAsync(source, ct);
            await _db.SaveChangesAsync(ct);
            await _publisher.PublishAsync(workspaceId, ct);
            await tx.CommitAsync(ct);

            _logger.LogInformation("instagram.profile.source.synced WorkspaceId={WorkspaceId} ConnectionId={ConnectionId} AccountId={AccountId}",
                workspaceId, connectionId, accountId);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task SyncFacebookPageAsync(string workspaceId, Guid connectionId, string pageId, string pageName, CancellationToken ct = default)
    {
        await using IDbContextTransaction tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            BusinessSource source = new BusinessSource
            {
                WorkspaceId = workspaceId,
                AuthorizationConnectionId = connectionId,
                Channel = Channel.Facebook,
                Kind = BusinessSourceKind.Profile,
                ExternalId = pageId,
                DisplayName = pageName,
                IsActive = true,
                MetadataSyncStatus = MetadataSyncStatus.Pending,
            };
            await _sources.UpsertAsync(source, ct);
            await _db.SaveChangesAsync(ct);
            await _publisher.PublishAsync(workspaceId, ct);
            await tx.CommitAsync(ct);

            _logger.LogInformation("facebook.page.source.synced WorkspaceId={WorkspaceId} ConnectionId={ConnectionId} PageId={PageId}",
                workspaceId, connectionId, pageId);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }
}
