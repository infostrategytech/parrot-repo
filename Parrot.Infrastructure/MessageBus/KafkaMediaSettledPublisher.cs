using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;

namespace Parrot.Infrastructure.MessageBus;

/// <summary>
/// Publishes to <c>Kafka:MediaSettledTopic</c>, keyed by message. The announcement is a convenience (the file is
/// already settled in MongoDB and the inbox polls), so a Kafka failure is logged and swallowed: throwing would
/// make the media job run again, find the file settled and skip it, and the announcement would still be lost.
/// </summary>
public sealed class KafkaMediaSettledPublisher(IMessageBus bus, IOptions<KafkaSettings> settings, ILogger<KafkaMediaSettledPublisher> logger)
    : IMediaSettledPublisher
{
    public async Task PublishAsync(MediaSettled settled, CancellationToken cancellationToken = default)
    {
        try
        {
            await bus.PublishAsync(settings.Value.MediaSettledTopic, settled.MessageId, settled, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("media.announce_failed Error={Error}; the inbox shows the file on its next poll", ex.GetType().Name);
        }
    }
}
