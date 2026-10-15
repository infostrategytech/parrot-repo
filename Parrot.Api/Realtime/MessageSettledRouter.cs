using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;
using Parrot.Application.Realtime;

namespace Parrot.Api.Realtime;

/// <summary>
/// Tells the owner's live connections that a customer's file finished loading (<c>mediaChanged</c>) or a queued
/// reply was sent (the <c>receiptsChanged</c> the inbox already answers by re-reading its open thread). Best effort,
/// as <see cref="MessagePersistedRouter"/> is: the work is already settled in MongoDB, so an offline owner, Redis
/// being down or a failed push changes nothing durable, and the inbox's poll shows it. Never throws (other than for
/// shutdown): a throwing handler would make the bus redeliver the event and hold up the partition.
/// </summary>
public sealed class MessageSettledRouter(IOnlineSessionStore sessions, IHubContext<MessagingHub> hub, ILogger<MessageSettledRouter> logger)
{
    public async Task RouteAsync(MessageSettled settled, CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<string> connections = await sessions.GetConnectionsAsync(settled.OwnerUserId, cancellationToken);
            if (connections.Count == 0) return;

            (string method, object payload) = settled.Kind == SettledKinds.Media
                ? (MessagingHub.MediaChangedMethod, new MediaChanged(settled.ConnectionId, settled.Customer, settled.MessageId, settled.Status))
                : (MessagingHub.ReceiptsChangedMethod, (object)new ReceiptsChanged(settled.ConnectionId, settled.Customer));
            await hub.Clients.Clients(connections).SendAsync(method, payload, cancellationToken);
            logger.LogInformation("realtime.settled Kind={Kind} MessageId={MessageId} Status={Status} Connections={Connections}",
                settled.Kind, settled.MessageId, settled.Status, connections.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "realtime.settled_push_failed Kind={Kind} MessageId={MessageId}; the inbox shows it on its next poll", settled.Kind, settled.MessageId);
        }
    }
}

/// <summary>
/// Reads <c>messages.settled</c> from new events only: live delivery never replays history. Each API instance reads
/// in a group of its own (the configured name plus the machine's), so every instance sees every event and pushes to
/// the browsers connected to it; a shared group would split the partitions and some owners would never hear about
/// their files. Old groups expire on their own.
/// </summary>
public sealed class MessageSettledDeliveryService(IMessageBus bus, MessageSettledRouter router, IOptions<KafkaSettings> kafka) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        bus.SubscribeAsync<MessageSettled>(
            kafka.Value.SettledTopic,
            router.RouteAsync,
            new SubscriptionOptions(GroupId: $"{kafka.Value.SettledConsumerGroupId}-{Environment.MachineName}", StartFromLatest: true),
            stoppingToken);
}
