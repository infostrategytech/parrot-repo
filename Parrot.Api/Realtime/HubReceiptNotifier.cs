using Microsoft.AspNetCore.SignalR;
using Parrot.Application.Conversations;
using Parrot.Application.Messaging;
using Parrot.Application.Realtime;

namespace Parrot.Api.Realtime;

/// <summary>
/// Pushes "receipts changed" to the owner's live connections, if they have any. The webhook that
/// records a receipt runs in this process, so it calls this directly rather than through the bus.
/// Best effort, as <see cref="MessagePersistedRouter"/> is: nothing here is durable.
/// </summary>
public sealed class HubReceiptNotifier(IOnlineSessionStore sessions, IHubContext<MessagingHub> hub,
    IRealtimeAudience audience, ILogger<HubReceiptNotifier> logger) : IReceiptNotifier
{
    public async Task ReceiptsChangedAsync(string owner, Guid connection, string? customer, CancellationToken ct)
    {
        try
        {
            IReadOnlyList<string> connections = await audience.ForConversationAsync(owner, await sessions.GetConnectionsAsync(owner, ct), connection);
            if (connections.Count == 0) return;
            await hub.Clients.Clients(connections).SendAsync(MessagingHub.ReceiptsChangedMethod, new ReceiptsChanged(connection, customer), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "realtime.receipt_push_failed; the inbox shows it on its next poll");
        }
    }
}
