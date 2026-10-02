namespace Parrot.Application.Messaging;

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

    /// <summary>
    /// Consumes <paramref name="topic"/> in batches: in order per Kafka key, keys in
    /// parallel. A batch's offsets are committed only once every message in it is
    /// handled; otherwise the batch is rewound and read again after a back-off, so
    /// <paramref name="handler"/> must be safe to run twice for one message.
    /// Throwing <see cref="Parrot.Application.Messaging.UnprocessableMessageException"/>
    /// dead-letters the message instead of retrying it.
    /// </summary>
    Task SubscribeKeyedAsync<TMessage>(
        string topic, Func<TMessage, CancellationToken, Task> handler, KeyedSubscriptionOptions options, CancellationToken cancellationToken);
}

/// <summary>How a keyed batch subscription consumes; the defaults are the worker's.</summary>
public sealed record KeyedSubscriptionOptions
{
    /// <summary>Where messages that can never be handled go, with the reason in a header.</summary>
    required public string DeadLetterTopic { get; init; }

    /// <summary>The consumer group; null uses <c>Kafka:ConsumerGroupId</c>.</summary>
    public string? GroupId { get; init; }

    public int MaxBatchSize { get; init; } = 500;

    /// <summary>How long to keep collecting after the first message of a batch arrives.</summary>
    public TimeSpan MaxBatchWait { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>How many keys (conversations) are handled at once.</summary>
    public int MaxParallelism { get; init; } = 32;

    /// <summary>The longest wait before reading a failed batch again; waits double up to this.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// On shutdown, how long the batch in hand may take to finish before it is
    /// abandoned uncommitted (and read again by the next consumer). Keep it under
    /// the host's ShutdownTimeout so the commit and close still fit.
    /// </summary>
    public TimeSpan ShutdownGrace { get; init; } = TimeSpan.FromSeconds(25);
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
