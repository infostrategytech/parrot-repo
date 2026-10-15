using Microsoft.AspNetCore.SignalR;
using Parrot.Application.Messaging;
using Parrot.Application.Realtime;

namespace Parrot.Api.Realtime;

/// <summary>
/// Tells the owner's live connections that one of a customer's files has finished loading. Best effort, as
/// <see cref="MessagePersistedRouter"/> is: the file is already settled in MongoDB, so an offline owner, Redis being
/// down or a failed push changes nothing durable, and the inbox's poll shows it. Never throws (other than for
/// shutdown): a throwing handler would make the bus redeliver the event and hold up the partition.
/// </summary>
public sealed class MediaSettledRouter(IOnlineSessionStore sessions, IHubContext<MessagingHub> hub, ILogger<MediaSettledRouter> logger)
{
    public async Task RouteAsync(MediaSettled settled, CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<string> connections = await sessions.GetConnectionsAsync(settled.OwnerUserId, cancellationToken);
            if (connections.Count == 0) return;
            await hub.Clients.Clients(connections).SendAsync(
                MessagingHub.MediaChangedMethod,
                new MediaChanged(settled.ConnectionId, settled.Customer, settled.MessageId, settled.Status),
                cancellationToken);
            logger.LogInformation("realtime.media_changed MessageId={MessageId} Status={Status} Connections={Connections}",
                settled.MessageId, settled.Status, connections.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "realtime.media_push_failed MessageId={MessageId}; the inbox shows the file on its next poll", settled.MessageId);
        }
    }
}
