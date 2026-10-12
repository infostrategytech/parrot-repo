using Microsoft.Extensions.Options;
using Parrot.Application.Agents;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;

namespace Parrot.Api.Realtime;

/// <summary>
/// Reads the agent events topic and hands each event to the router. It reads
/// in a group of its own from the beginning of the topic: every event is kept
/// in the activity store, so nothing published while the API was down may be
/// skipped, and the store's unique key makes a replay harmless.
/// </summary>
/// <remarks>
/// With several API instances each gets a share of the partitions; the store
/// is shared, so the figures are right, but a push reaches only the owners
/// connected to the instance that read the event until SignalR has a backplane.
/// </remarks>
public sealed class AgentEventsConsumer(
    IMessageBus bus,
    AgentEventsRouter router,
    IOptions<KafkaSettings> kafka) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        bus.SubscribeAsync<AgentEventEnvelope>(
            kafka.Value.AgentEventsTopic,
            router.RouteAsync,
            new SubscriptionOptions(GroupId: kafka.Value.AgentEventsConsumerGroupId, StartFromLatest: false),
            stoppingToken);
}
