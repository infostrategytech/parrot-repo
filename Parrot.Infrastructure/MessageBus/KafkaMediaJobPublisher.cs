using Microsoft.Extensions.Options;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;

namespace Parrot.Infrastructure.MessageBus;

/// <summary>Publishes to <c>Kafka:MediaTopic</c>. Keyed by message, not conversation: files have no order to keep.</summary>
public sealed class KafkaMediaJobPublisher(IMessageBus bus, IOptions<KafkaSettings> settings) : IMediaJobPublisher
{
    public Task PublishAsync(MediaJob job, CancellationToken cancellationToken = default) =>
        bus.PublishAsync(settings.Value.MediaTopic, job.MessageId, job, cancellationToken);
}
