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
