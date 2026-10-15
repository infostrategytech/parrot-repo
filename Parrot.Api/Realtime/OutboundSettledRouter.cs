using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;
using Parrot.Application.Realtime;

namespace Parrot.Api.Realtime;

/// <summary>
/// Tells the owner's open inbox that a queued reply has been sent or has failed, so it re-reads the thread. It
/// pushes the same message the delivery receipts do (<see cref="ReceiptsChanged"/>), which the inbox already
/// answers by re-reading the thread it has open. Best effort, and never throws other than for shutdown.
/// </summary>
public sealed class OutboundSettledRouter(IOnlineSessionStore sessions, IHubContext<MessagingHub> hub, ILogger<OutboundSettledRouter> logger)
{
    public async Task RouteAsync(OutboundSettled settled, CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<string> connections = await sessions.GetConnectionsAsync(settled.OwnerUserId, cancellationToken);
            if (connections.Count == 0) return;
            await hub.Clients.Clients(connections).SendAsync(
                MessagingHub.ReceiptsChangedMethod, new ReceiptsChanged(settled.ConnectionId, settled.Customer), cancellationToken);
            logger.LogInformation("realtime.outbound_settled MessageId={MessageId} Status={Status} Connections={Connections}",
                settled.MessageId, settled.Status, connections.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "realtime.outbound_push_failed MessageId={MessageId}; the inbox shows it on its next poll", settled.MessageId);
        }
    }
}

/// <summary>
/// Reads <c>messages.outbound.settled</c> from new events only, in a group per API instance so every instance sees
/// every event (see <see cref="MediaSettledDeliveryService"/>).
/// </summary>
public sealed class OutboundSettledDeliveryService(IMessageBus bus, OutboundSettledRouter router, IOptions<KafkaSettings> kafka) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        bus.SubscribeAsync<OutboundSettled>(
            kafka.Value.OutboundSettledTopic,
            router.RouteAsync,
            new SubscriptionOptions(GroupId: $"{kafka.Value.OutboundSettledConsumerGroupId}-{Environment.MachineName}", StartFromLatest: true),
            stoppingToken);
}
