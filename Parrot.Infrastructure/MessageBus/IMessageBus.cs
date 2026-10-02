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
    Task SubscribeAsync<TMessage>(string topic, Func<TMessage, CancellationToken, Task> handler, CancellationToken cancellationToken)
        => SubscribeAsync(topic, handler, SubscriptionOptions.Default, cancellationToken);

    /// <summary>
    /// As above, with a consumer group and start position of the subscriber's
    /// own, for a reader that must not share the default group's partitions.
    /// </summary>
    Task SubscribeAsync<TMessage>(
        string topic, Func<TMessage, CancellationToken, Task> handler, SubscriptionOptions options, CancellationToken cancellationToken);
}

/// <summary>How one subscription consumes, where it differs from the bus defaults.</summary>
/// <param name="GroupId">
/// The consumer group; null uses <c>Kafka:ConsumerGroupId</c>. Two subscribers
/// in one group split the partitions between them, so each sees only part of
/// the topic: give an independent reader its own group.
/// </param>
/// <param name="StartFromLatest">
/// Where a group with no committed offset starts. False reads the whole topic
/// (right for a worker that must process everything); true starts at new
/// messages only (right for live delivery, which must not replay history).
/// </param>
public sealed record SubscriptionOptions(string? GroupId = null, bool StartFromLatest = false)
{
    public static SubscriptionOptions Default { get; } = new();
}
