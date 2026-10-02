using Microsoft.Extensions.Options;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;
using Parrot.Infrastructure.MessageBus;

namespace Parrot.Api.Realtime;

/// <summary>
/// Reads <c>messages.persisted</c> and hands each event to the router.
///
/// It reads in a consumer group of its own, so it sees every partition
/// rather than splitting them with the worker, and a new group starts at new
/// messages: live delivery must not replay history, which clients fetch
/// themselves.
/// </summary>
/// <remarks>
/// With one API instance this group sees every event. Once there are several,
/// each instance gets a share of the partitions while users connect to any
/// instance, so pushes need a SignalR backplane (Redis or Azure SignalR) before
/// the API scales out.
/// </remarks>
public sealed class RealtimeDeliveryService(
    IMessageBus bus,
    MessagePersistedRouter router,
    IOptions<KafkaSettings> kafka) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        bus.SubscribeAsync<MessagePersisted>(
            kafka.Value.PersistedTopic,
            router.RouteAsync,
            new SubscriptionOptions(GroupId: kafka.Value.RealtimeConsumerGroupId, StartFromLatest: true),
            stoppingToken);
}
