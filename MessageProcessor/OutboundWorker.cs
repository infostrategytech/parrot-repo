using Microsoft.Extensions.Options;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;

namespace MessageProcessor;

/// <summary>
/// Reads <c>messages.outbound</c> and hands each queued reply to its channel through <see cref="OutboundFileProcessor"/>.
/// Its own consumer group, so a slow channel never delays storing incoming messages; run more instances to send more
/// at once. The key is the thread, so one customer's replies go out in the order they were queued. A job that can
/// never succeed goes to the outbound dead-letter topic.
/// </summary>
public sealed class OutboundWorker(IMessageBus bus, OutboundFileProcessor processor, IOptions<KafkaSettings> kafka) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        bus.SubscribeKeyedAsync<OutboundJob>(
            kafka.Value.OutboundTopic,
            processor.ProcessAsync,
            new KeyedSubscriptionOptions
            {
                GroupId = kafka.Value.OutboundConsumerGroupId,
                DeadLetterTopic = kafka.Value.OutboundDeadLetterTopic,
                // Each send may wait on a channel and retry an upload: fewer at once than messages, in small batches.
                MaxParallelism = 8,
                MaxBatchSize = 16,
            },
            stoppingToken);
}
