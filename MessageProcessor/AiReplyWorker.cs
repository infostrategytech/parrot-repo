using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;
using Parrot.Infrastructure.MessageBus;

namespace MessageProcessor;

/// <summary>
/// Consumes <c>ai.reply.requested</c> events published by Omni-AI and hands each one
/// to <see cref="AiReplyProcessor"/>. Messages with the same key (workspace + connection +
/// recipient) are processed in order, so replies within a conversation are delivered in
/// sequence. Events that can never be processed (missing connection, bad payload, a send the
/// channel refuses) go to the dead-letter topic rather than blocking the partition.
/// </summary>
public sealed class AiReplyWorker(IMessageBus bus, AiReplyProcessor processor, IOptions<KafkaSettings> kafka, ILogger<AiReplyWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await LogTopicsAsync();
        await bus.SubscribeKeyedAsync<AiReplyRequested>(
            kafka.Value.AiReplyTopic,
            processor.ProcessAsync,
            new KeyedSubscriptionOptions
            {
                GroupId = kafka.Value.AiReplyConsumerGroupId,
                DeadLetterTopic = kafka.Value.AiReplyDeadLetterTopic,
            },
            stoppingToken);
    }

    // Said once at start. Topics are never created automatically: without the dead-letter topic, a reply that can't be
    // sent can't be set aside either, and its batch is read again for ever.
    private async Task LogTopicsAsync()
    {
        if (bus is not KafkaMessageBus broker) return;
        foreach (string topic in new[] { kafka.Value.AiReplyTopic, kafka.Value.AiReplyDeadLetterTopic })
        {
            try
            {
                int? partitions = await broker.PartitionCountAsync(topic, TimeSpan.FromSeconds(10));
                if (partitions is null)
                    logger.LogError(
                        "ai_reply.topic_missing Topic={Topic} KafkaBootstrap={KafkaBootstrap}: topics are not created automatically; add it to kafka-topic-init.",
                        topic, kafka.Value.BootstrapServers);
                else
                    logger.LogInformation("ai_reply.topic_ok Topic={Topic} Partitions={Partitions}", topic, partitions);
            }
            catch (KafkaException ex)
            {
                logger.LogError(ex, "ai_reply.topic_check_failed Topic={Topic} KafkaBootstrap={KafkaBootstrap} Reason={Reason}",
                    topic, kafka.Value.BootstrapServers, ex.Error.Reason);
            }
        }
    }
}
