using Microsoft.Extensions.Options;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;

namespace Parrot.Api.Realtime;

/// <summary>
/// Reads <c>messages.media.settled</c> and hands each event to the router. Live delivery only: it starts at new
/// events, never replaying history. Each API instance reads in a group of its own (the configured name plus the
/// machine's), so every instance sees every event and pushes to the browsers connected to it; a shared group would
/// split the partitions and some owners would never hear about their files. Old groups expire on their own.
/// </summary>
public sealed class MediaSettledDeliveryService(IMessageBus bus, MediaSettledRouter router, IOptions<KafkaSettings> kafka) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        bus.SubscribeAsync<MediaSettled>(
            kafka.Value.MediaSettledTopic,
            router.RouteAsync,
            new SubscriptionOptions(GroupId: $"{kafka.Value.MediaSettledConsumerGroupId}-{Environment.MachineName}", StartFromLatest: true),
            stoppingToken);
}
