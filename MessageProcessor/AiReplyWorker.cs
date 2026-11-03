using Microsoft.Extensions.Options;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;

namespace MessageProcessor;

/// <summary>
/// Consumes <c>ai.reply.requested</c> events published by Omni-AI and hands each one
/// to <see cref="AiReplyProcessor"/>. Messages with the same key (workspace + connection +
/// recipient) are processed in order, so replies within a conversation are delivered in
/// sequence. Events that can never be processed (missing connection, bad payload) go to
/// the dead-letter topic rather than blocking the partition.
/// </summary>
public sealed class AiReplyWorker(IMessageBus bus, AiReplyProcessor processor, IOptions<KafkaSettings> kafka) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        bus.SubscribeKeyedAsync<AiReplyRequested>(
            kafka.Value.AiReplyTopic,
            processor.ProcessAsync,
            new KeyedSubscriptionOptions
            {
                GroupId = kafka.Value.AiReplyConsumerGroupId,
                DeadLetterTopic = kafka.Value.AiReplyDeadLetterTopic,
            },
            stoppingToken);
}
