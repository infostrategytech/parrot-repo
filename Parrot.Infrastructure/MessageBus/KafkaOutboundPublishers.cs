using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;

namespace Parrot.Infrastructure.MessageBus;

/// <summary>Publishes to <c>Kafka:OutboundTopic</c>, keyed by thread. It throws when Kafka does not take the job, so the API can fail the reply cleanly.</summary>
public sealed class KafkaOutboundJobPublisher(IMessageBus bus, IOptions<KafkaSettings> settings) : IOutboundJobPublisher
{
    public Task PublishAsync(OutboundJob job, CancellationToken cancellationToken = default) =>
        bus.PublishAsync(settings.Value.OutboundTopic, job.ThreadKey, job, cancellationToken);
}

/// <summary>Publishes to <c>Kafka:OutboundSettledTopic</c>. A Kafka failure is logged and swallowed: the reply is already settled and the inbox polls.</summary>
public sealed class KafkaOutboundSettledPublisher(IMessageBus bus, IOptions<KafkaSettings> settings, ILogger<KafkaOutboundSettledPublisher> logger)
    : IOutboundSettledPublisher
{
    public async Task PublishAsync(OutboundSettled settled, CancellationToken cancellationToken = default)
    {
        try
        {
            await bus.PublishAsync(settings.Value.OutboundSettledTopic, settled.MessageId, settled, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("outbound.announce_failed Error={Error}; the inbox shows the reply's status on its next poll", ex.GetType().Name);
        }
    }
}
