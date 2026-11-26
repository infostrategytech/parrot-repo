using Microsoft.EntityFrameworkCore;
using Parrot.Application.Teams;
using Parrot.Domain.Entities;
using Parrot.Domain.Enums;
using Parrot.Infrastructure.Data;

namespace Parrot.Infrastructure.Teams;

public sealed class TeamRepository(ApplicationDbContext db) : ITeamRepository
{
    public async Task<IReadOnlyList<WorkspaceGrant>> ListGrantsAsync(string ownerId, string userId) =>
        await db.WorkspaceGrants.AsNoTracking()
            .Where(g => g.OwnerId == ownerId && g.UserId == userId)
            .OrderBy(g => g.CreatedAt)
            .ToListAsync();

    public async Task<IReadOnlyList<WorkspaceGrant>> ListTeamGrantsAsync(string ownerId) =>
        await db.WorkspaceGrants.AsNoTracking()
            .Where(g => g.OwnerId == ownerId)
            .OrderBy(g => g.CreatedAt)
            .ToListAsync();

    public async Task<IReadOnlyList<WorkspaceGrant>> ListGrantsForUserAsync(string userId) =>
        await db.WorkspaceGrants.AsNoTracking()
            .Where(g => g.UserId == userId)
            .OrderBy(g => g.CreatedAt)
            .ToListAsync();

    // One transaction, so a person is never briefly left with no grants (out
    // of the team) or with both the old and the new ones.
    public async Task ReplaceGrantsAsync(string ownerId, string userId, IReadOnlyList<WorkspaceGrant> grants)
    {
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = await db.Database.BeginTransactionAsync();
        await db.WorkspaceGrants.Where(g => g.OwnerId == ownerId && g.UserId == userId).ExecuteDeleteAsync();
        db.WorkspaceGrants.AddRange(grants);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    public async Task<IReadOnlyList<OwnerRole>> ListRolesAsync(string ownerId) =>
        await db.OwnerRoles.AsNoTracking().Where(r => r.OwnerId == ownerId).OrderBy(r => r.Name).ToListAsync();

    public async Task<OwnerRole?> GetRoleAsync(string ownerId, Guid roleId) =>
        await db.OwnerRoles.FirstOrDefaultAsync(r => r.Id == roleId && r.OwnerId == ownerId);

    public async Task<bool> TryAddRoleAsync(OwnerRole role)
    {
        db.OwnerRoles.Add(role);
        return await TrySaveAsync(role);
    }

    public async Task<bool> TryUpdateRoleAsync(OwnerRole role)
    {
        db.OwnerRoles.Update(role);
        return await TrySaveAsync(role);
    }

    public async Task DeleteRoleAsync(OwnerRole role)
    {
        db.OwnerRoles.Remove(role);
        await db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<string>> ListRoleHoldersAsync(string ownerId, Guid roleId) =>
        await db.WorkspaceGrants.Where(g => g.OwnerId == ownerId && g.CustomRoleId == roleId)
            .Select(g => g.UserId).Distinct().ToListAsync();

    // The (OwnerId, NormalizedName) index answers a taken name, including one
    // another request saved a moment ago.
    private async Task<bool> TrySaveAsync(OwnerRole role)
    {
        try
        {
            await db.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            db.Entry(role).State = EntityState.Detached;
            return false;
        }
    }

    public async Task<TeamInvite?> GetInviteAsync(string ownerId, Guid inviteId) =>
        await db.TeamInvites.FirstOrDefaultAsync(i => i.Id == inviteId && i.OwnerId == ownerId);

    public async Task<TeamInvite?> FindInviteByTokenHashAsync(string tokenHash) =>
        await db.TeamInvites.FirstOrDefaultAsync(i => i.TokenHash == tokenHash);

    public async Task<TeamInvite?> FindPendingInviteAsync(string ownerId, string normalizedEmail, DateTime now) =>
        await db.TeamInvites.FirstOrDefaultAsync(i => i.OwnerId == ownerId && i.NormalizedEmail == normalizedEmail
            && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now);

    public async Task<IReadOnlyList<TeamInvite>> ListPendingInvitesAsync(string ownerId, DateTime now) =>
        await db.TeamInvites.AsNoTracking()
            .Where(i => i.OwnerId == ownerId && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync();

    public async Task AddInviteAsync(TeamInvite invite)
    {
        db.TeamInvites.Add(invite);
        await db.SaveChangesAsync();
    }

    public async Task UpdateInviteAsync(TeamInvite invite)
    {
        db.TeamInvites.Update(invite);
        await db.SaveChangesAsync();
    }
}

/// <summary>Reads the four connection tables for one workspace, disabled connections included: their history is still there to read.</summary>
public sealed class WorkspaceConnectionDirectory(ApplicationDbContext db) : IWorkspaceConnectionDirectory
{
    public async Task<IReadOnlyList<(Guid Id, MessagingPlatform Platform)>> ListAsync(string workspaceId)
    {
        List<(Guid, MessagingPlatform)> connections = [];
        connections.AddRange((await db.WhatsAppConnections.Where(c => c.WorkspaceId == workspaceId).Select(c => c.Id).ToListAsync())
            .Select(id => (id, MessagingPlatform.WhatsApp)));
        connections.AddRange((await db.InstagramConnections.Where(c => c.WorkspaceId == workspaceId).Select(c => c.Id).ToListAsync())
            .Select(id => (id, MessagingPlatform.Instagram)));
        connections.AddRange((await db.FacebookConnections.Where(c => c.WorkspaceId == workspaceId).Select(c => c.Id).ToListAsync())
            .Select(id => (id, MessagingPlatform.Facebook)));
        connections.AddRange((await db.TikTokConnections.Where(c => c.WorkspaceId == workspaceId).Select(c => c.Id).ToListAsync())
            .Select(id => (id, MessagingPlatform.TikTok)));
        return connections;
    }

    // A connection that's been turned off has no one answering it, so it gets no link.
    public async Task<string?> ChatLinkAsync(string workspaceId, Guid connectionId, CancellationToken ct = default)
    {
        string? phone = await db.WhatsAppConnections.AsNoTracking()
            .Where(c => c.Id == connectionId && c.WorkspaceId == workspaceId && c.DisabledAt == null).Select(c => c.DisplayPhoneNumber).FirstOrDefaultAsync(ct);
        if (phone is not null) return ChatLink.For(MessagingPlatform.WhatsApp, phone);

        string? username = await db.InstagramConnections.AsNoTracking()
            .Where(c => c.Id == connectionId && c.WorkspaceId == workspaceId && c.DisabledAt == null).Select(c => c.Username).FirstOrDefaultAsync(ct);
        if (username is not null) return ChatLink.For(MessagingPlatform.Instagram, username);

        string? page = await db.FacebookConnections.AsNoTracking()
            .Where(c => c.Id == connectionId && c.WorkspaceId == workspaceId && c.DisabledAt == null).Select(c => c.PageId).FirstOrDefaultAsync(ct);
        return ChatLink.For(MessagingPlatform.Facebook, page);
    }
}
