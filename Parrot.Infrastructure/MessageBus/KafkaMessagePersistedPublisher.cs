using Microsoft.Extensions.Options;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;

namespace Parrot.Infrastructure.MessageBus;

/// <summary>Publishes to <c>Kafka:PersistedTopic</c>, keyed by conversation so one conversation's events stay in order.</summary>
public sealed class KafkaMessagePersistedPublisher(IMessageBus bus, IOptions<KafkaSettings> settings) : IMessagePersistedPublisher
{
    public Task PublishAsync(MessagePersisted persisted, CancellationToken cancellationToken = default) =>
        bus.PublishAsync(settings.Value.PersistedTopic, persisted.Data.ConversationKey, persisted, cancellationToken);
}
