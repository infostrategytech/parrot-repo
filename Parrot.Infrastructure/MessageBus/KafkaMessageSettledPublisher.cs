using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;

namespace Parrot.Infrastructure.MessageBus;

/// <summary>
/// Publishes to <c>Kafka:SettledTopic</c>, keyed by message. The announcement is a convenience (the work is already
/// settled in MongoDB and the inbox polls), so a Kafka failure is logged and swallowed: throwing would make the job
/// run again, find the work settled and skip it, and the announcement would still be lost.
/// </summary>
public sealed class KafkaMessageSettledPublisher(IMessageBus bus, IOptions<KafkaSettings> settings, ILogger<KafkaMessageSettledPublisher> logger)
    : IMessageSettledPublisher
{
    public async Task PublishAsync(MessageSettled settled, CancellationToken cancellationToken = default)
    {
        try
        {
            await bus.PublishAsync(settings.Value.SettledTopic, settled.MessageId, settled, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("settled.announce_failed Kind={Kind} Error={Error}; the inbox shows it on its next poll", settled.Kind, ex.GetType().Name);
        }
    }
}
