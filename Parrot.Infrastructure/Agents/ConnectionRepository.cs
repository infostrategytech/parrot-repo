using Microsoft.EntityFrameworkCore;
using Parrot.Application.Agents;
using Parrot.Domain.Entities;
using Parrot.Infrastructure.Data;

namespace Parrot.Infrastructure.Agents;

public class ConnectionRepository : IConnectionRepository
{
    private readonly ApplicationDbContext _db;

    public ConnectionRepository(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<ConnectionInfo?> FindAsync(Guid id, CancellationToken cancellationToken = default)
    {
        WhatsAppConnection? whatsApp = await _db.WhatsAppConnections
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

        if (whatsApp is not null)
        {
            string ownerId = await OwnerIdAsync(whatsApp.WorkspaceId, cancellationToken);
            return new ConnectionInfo(whatsApp.Id, whatsApp.WorkspaceId, ownerId, "whatsapp", whatsApp.DisabledAt.HasValue);
        }

        FacebookConnection? facebook = await _db.FacebookConnections
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

        if (facebook is not null)
        {
            string ownerId = await OwnerIdAsync(facebook.WorkspaceId, cancellationToken);
            return new ConnectionInfo(facebook.Id, facebook.WorkspaceId, ownerId, "facebook", facebook.DisabledAt.HasValue);
        }

        InstagramConnection? instagram = await _db.InstagramConnections
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

        if (instagram is not null)
        {
            string ownerId = await OwnerIdAsync(instagram.WorkspaceId, cancellationToken);
            return new ConnectionInfo(instagram.Id, instagram.WorkspaceId, ownerId, "instagram", instagram.DisabledAt.HasValue);
        }

        TikTokConnection? tikTok = await _db.TikTokConnections
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

        if (tikTok is not null)
        {
            string ownerId = await OwnerIdAsync(tikTok.WorkspaceId, cancellationToken);
            return new ConnectionInfo(tikTok.Id, tikTok.WorkspaceId, ownerId, "tiktok", tikTok.DisabledAt.HasValue);
        }

        return null;
    }

    private async Task<string> OwnerIdAsync(string workspaceId, CancellationToken cancellationToken)
    {
        Workspace? workspace = await _db.Workspaces
            .FirstOrDefaultAsync(w => w.Id == workspaceId, cancellationToken);

        // A connection whose workspace no longer exists is treated as not found by the caller.
        return workspace?.OwnerId ?? string.Empty;
    }
}
