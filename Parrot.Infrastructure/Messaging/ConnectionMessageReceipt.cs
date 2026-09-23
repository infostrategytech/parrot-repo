using Microsoft.EntityFrameworkCore;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Integrations;
using Parrot.Infrastructure.Data;
using Parrot.Domain.Enums;

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
        if (message.Platform == MessagingPlatform.Facebook)
        {
            int facebookUpdated = await db.FacebookConnections
                .Where(c => c.Id == message.ConnectionId && c.OwnerUserId == message.OwnerUserId
                    && c.DisabledAt == null && c.WebhookVerifiedAt != null && c.PageId == message.ChannelId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(c => c.UpdatedAt, now), cancellationToken);
            if (facebookUpdated == 0)
                throw new InvalidOperationException("Facebook message was stored, but its Page connection did not match PostgreSQL.");
            return;
        }

        int updated = await db.WhatsAppConnections
            .Where(c => c.Id == message.ConnectionId && c.OwnerUserId == message.OwnerUserId
                && c.DisabledAt == null && c.WebhookVerifiedAt != null
                && c.WhatsAppBusinessAccountId == message.ExternalAccountId && c.PhoneNumberId == message.ChannelId
                && c.FirstMessageReceivedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.FirstMessageReceivedAt, now)
                .SetProperty(c => c.UpdatedAt, now), cancellationToken);
        if (updated > 0) return;

        // Duplicate Kafka deliveries are successful only when the matching receipt already exists.
        // A missing/mismatched row must not silently commit the Kafka offset after Mongo succeeds.
        var connection = await db.WhatsAppConnections.AsNoTracking()
            .Where(c => c.Id == message.ConnectionId)
            .Select(c => new
            {
                c.OwnerUserId, c.DisabledAt, c.WebhookVerifiedAt, c.FirstMessageReceivedAt,
                c.WhatsAppBusinessAccountId, c.PhoneNumberId,
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (connection is null)
            throw new InvalidOperationException("Message was stored in MongoDB, but its WhatsApp connection was not found in PostgreSQL. Check that the API and ingestor use the same ConnectionStrings__DefaultConnection.");
        if (connection.OwnerUserId != message.OwnerUserId
            || connection.WhatsAppBusinessAccountId != message.ExternalAccountId
            || connection.PhoneNumberId != message.ChannelId)
            throw new InvalidOperationException("Message was stored in MongoDB, but its owner or Meta account IDs do not match the PostgreSQL connection. Receipt was not recorded.");
        // Connections disabled after the event was queued should not block the consumer.
        if (connection.DisabledAt.HasValue) return;
        if (connection.WebhookVerifiedAt is null)
            throw new InvalidOperationException("Message was stored in MongoDB, but its PostgreSQL connection is not webhook-verified. Receipt was not recorded.");
        if (connection.FirstMessageReceivedAt.HasValue) return;
        throw new InvalidOperationException("Message was stored in MongoDB, but the connection receipt was not updated. Retry the event.");
    }
}
