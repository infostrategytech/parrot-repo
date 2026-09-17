using Microsoft.EntityFrameworkCore;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Integrations;
using Parrot.Infrastructure.Data;

namespace Parrot.Infrastructure.Messaging;

public sealed class ConnectionMessageReceipt : IConnectionMessageReceipt
{
    private readonly IDbContextFactory<ApplicationDbContext> _contexts;

    public ConnectionMessageReceipt(IDbContextFactory<ApplicationDbContext> contexts)
    {
        _contexts = contexts;
    }

    public async Task RecordAsync(IncomingMessage message, CancellationToken cancellationToken)
    {
        await using ApplicationDbContext db = await _contexts.CreateDbContextAsync(cancellationToken);
        DateTime now = DateTime.UtcNow;
        await db.WhatsAppConnections
            .Where(c => c.Id == message.ConnectionId && c.OwnerUserId == message.OwnerUserId
                && c.DisabledAt == null && c.WebhookVerifiedAt != null
                && c.WhatsAppBusinessAccountId == message.ExternalAccountId && c.PhoneNumberId == message.ChannelId
                && c.FirstMessageReceivedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.FirstMessageReceivedAt, now)
                .SetProperty(c => c.UpdatedAt, now), cancellationToken);
    }
}
