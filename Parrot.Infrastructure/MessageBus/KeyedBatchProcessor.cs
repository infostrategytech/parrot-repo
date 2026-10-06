using Parrot.Application.Messaging;

namespace Parrot.Infrastructure.MessageBus;

/// <summary>One consumed message with where it came from in the topic.</summary>
public sealed record BatchItem<T>(string? Key, int Partition, long Offset, T Message);

/// <summary>
/// Runs a batch so that each key (a conversation) is handled in order, one
/// message after another, while different keys run in parallel up to a limit.
/// Kafka keeps one key in one partition, so the batch's order for a key is
/// its partition order.
///
/// It knows nothing about Kafka, so ordering and commits can be tested on
/// their own. The caller commits <see cref="CommitOffsets"/> only after
/// <see cref="ProcessAsync"/> returns; if it throws, nothing is committed and
/// the whole batch is consumed again, which the handlers' duplicate checks
/// make safe.
/// </summary>
public sealed class KeyedBatchProcessor(int maxParallelism)
{
    public async Task ProcessAsync<T>(
        IReadOnlyList<BatchItem<T>> batch, Func<BatchItem<T>, CancellationToken, Task> handle, CancellationToken cancellationToken)
    {
        // GroupBy keeps first-seen order within each group, which is batch order.
        IEnumerable<IGrouping<string, BatchItem<T>>> groups = batch.GroupBy(item => item.Key ?? string.Empty);

        await Parallel.ForEachAsync(
            groups,
            new ParallelOptions { MaxDegreeOfParallelism = maxParallelism, CancellationToken = cancellationToken },
            async (group, ct) =>
            {
                foreach (BatchItem<T> item in group)
                {
                    // A failure stops this key here: later messages for it must
                    // not be handled before the failed one.
                    await handle(item, ct);
                }
            });
    }

    /// <summary>
    /// The offset to commit for each partition: one past the highest offset in
    /// the batch, which is Kafka's "next message to read" convention.
    /// </summary>
    public static IReadOnlyDictionary<int, long> CommitOffsets<T>(IReadOnlyList<BatchItem<T>> batch) =>
        batch.GroupBy(item => item.Partition).ToDictionary(g => g.Key, g => g.Max(item => item.Offset) + 1);

    /// <summary>The offset to rewind each partition to so the whole batch is read again.</summary>
    public static IReadOnlyDictionary<int, long> RewindOffsets<T>(IReadOnlyList<BatchItem<T>> batch) =>
        batch.GroupBy(item => item.Partition).ToDictionary(g => g.Key, g => g.Min(item => item.Offset));
}

/// <summary>
/// What happens when handling one message fails. Temporary failures (any
/// exception but <see cref="UnprocessableMessageException"/>) are retried
/// after 200 ms, 1 s and 5 s; if they still fail, the batch fails and is read
/// again later. An unprocessable message is dead-lettered at once and counts
/// as handled. A failure to dead-letter is itself temporary.
/// </summary>
public sealed class MessageFailurePolicy(Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)];

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    /// <param name="onRetry">Called before each retry, with the attempt that failed (1-based).</param>
    public async Task HandleAsync<T>(
        BatchItem<T> item,
        Func<BatchItem<T>, CancellationToken, Task> handle,
        Func<BatchItem<T>, UnprocessableMessageException, CancellationToken, Task> deadLetter,
        Action<BatchItem<T>, int, Exception>? onRetry,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                try
                {
                    await handle(item, cancellationToken);
                }
                catch (UnprocessableMessageException ex)
                {
                    // Inside the outer try, so a failure to dead-letter is retried below.
                    await deadLetter(item, ex, cancellationToken);
                }

                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt <= RetryDelays.Length)
            {
                onRetry?.Invoke(item, attempt, ex);
                await _delay(RetryDelays[attempt - 1], cancellationToken);
            }
        }
    }
}
