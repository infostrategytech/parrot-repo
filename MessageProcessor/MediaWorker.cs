using Microsoft.Extensions.Options;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;

namespace MessageProcessor;

/// <summary>
/// Reads <c>messages.media</c> and copies each customer file into private storage through
/// <see cref="MediaJobProcessor"/>. Its own consumer group, so a slow download never delays storing messages;
/// run more instances to copy more files at once (the topic's partitions are the limit). A job that can never
/// succeed goes to the media dead-letter topic.
/// </summary>
public sealed class MediaWorker(IMessageBus bus, MediaJobProcessor processor, IOptions<KafkaSettings> kafka) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        bus.SubscribeKeyedAsync<MediaJob>(
            kafka.Value.MediaTopic,
            processor.ProcessAsync,
            new KeyedSubscriptionOptions
            {
                GroupId = kafka.Value.MediaConsumerGroupId,
                DeadLetterTopic = kafka.Value.MediaDeadLetterTopic,
                // Each job may wait on a slow platform and retry: fewer at once than messages, and small batches.
                MaxParallelism = 8,
                MaxBatchSize = 32,
            },
            stoppingToken);
}
