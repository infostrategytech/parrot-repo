namespace Parrot.Infrastructure.MessageBus;

public interface IMessageBus
{
    /// <summary>Serializes <paramref name="message"/> and publishes it to <paramref name="topic"/>.</summary>
    /// <param name="key">Partition key; messages sharing a key keep their relative order.</param>
    Task PublishAsync<TMessage>(string topic, string? key, TMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Consumes <paramref name="topic"/> until <paramref name="cancellationToken"/> is cancelled.
    /// A message is acknowledged only after <paramref name="handler"/> completes; a throwing handler
    /// causes the message to be redelivered.
    /// </summary>
    Task SubscribeAsync<TMessage>(string topic, Func<TMessage, CancellationToken, Task> handler, CancellationToken cancellationToken);
}
