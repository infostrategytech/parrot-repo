using Microsoft.Extensions.Options;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;
using Parrot.Infrastructure.MessageBus;

namespace MessageProcessor;

/// <summary>
/// Reads <c>messages.inbound</c> and hands each message to
/// <see cref="InboundMessageProcessor"/>: in order within a conversation,
/// conversations in parallel, committed only when a whole batch is stored and
/// announced. Unprocessable messages go to the dead-letter topic.
/// </summary>
public sealed class Worker(IMessageBus bus, InboundMessageProcessor processor, IOptions<KafkaSettings> kafka) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        bus.SubscribeKeyedAsync<InboundMessage>(
            kafka.Value.InboundTopic,
            processor.ProcessAsync,
            new KeyedSubscriptionOptions
            {
                GroupId = kafka.Value.ProcessorConsumerGroupId,
                DeadLetterTopic = kafka.Value.InboundDeadLetterTopic,
            },
            stoppingToken);
}
