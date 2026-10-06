using System.Collections.Concurrent;
using Parrot.Application.Messaging;
using Parrot.Infrastructure.MessageBus;

namespace Test.Parrot.Api.Tests.Services.Messaging;

public class KeyedBatchProcessorTests
{
    private static BatchItem<string> Item(string key, int partition, long offset) => new(key, partition, offset, $"{key}#{offset}");

    // Ada and Tunde in one batch; each conversation's messages must stay in their order.
    private static readonly IReadOnlyList<BatchItem<string>> Batch =
    [
        Item("ada", 0, 10), Item("tunde", 1, 20), Item("ada", 0, 11), Item("tunde", 1, 21), Item("ada", 0, 12),
    ];

    [Fact]
    public async Task EachConversationIsHandledInOrder()
    {
        ConcurrentDictionary<string, ConcurrentQueue<long>> seen = new();

        await new KeyedBatchProcessor(32).ProcessAsync(Batch, async (item, _) =>
        {
            // Jitter, so a processor that ran one key's messages in parallel would show it.
            await Task.Delay(Random.Shared.Next(1, 15));
            seen.GetOrAdd(item.Key!, _ => new()).Enqueue(item.Offset);
        }, CancellationToken.None);

        Assert.Equal([10L, 11L, 12L], seen["ada"]);
        Assert.Equal([20L, 21L], seen["tunde"]);
    }

    [Fact]
    public async Task DifferentConversationsRunAtTheSameTime()
    {
        int running = 0, peak = 0;
        TaskCompletionSource bothStarted = new();

        await new KeyedBatchProcessor(32).ProcessAsync([Item("ada", 0, 1), Item("tunde", 1, 1)], async (_, _) =>
        {
            int now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            if (now == 2) bothStarted.TrySetResult();
            await Task.WhenAny(bothStarted.Task, Task.Delay(2000));
            Interlocked.Decrement(ref running);
        }, CancellationToken.None);

        Assert.Equal(2, peak);
    }

    [Fact]
    public async Task TheParallelismLimitIsRespected()
    {
        int running = 0, peak = 0;
        BatchItem<string>[] batch = Enumerable.Range(0, 20).Select(i => Item($"c{i}", i % 3, i)).ToArray();

        await new KeyedBatchProcessor(4).ProcessAsync(batch, async (_, _) =>
        {
            InterlockedMax(ref peak, Interlocked.Increment(ref running));
            await Task.Delay(10);
            Interlocked.Decrement(ref running);
        }, CancellationToken.None);

        Assert.InRange(peak, 1, 4);
    }

    // A failed message must not be overtaken by the next one in its conversation.
    [Fact]
    public async Task AFailureStopsThatConversationAndFailsTheBatch()
    {
        ConcurrentBag<long> handled = new();

        await Assert.ThrowsAsync<IOException>(() => new KeyedBatchProcessor(32).ProcessAsync(Batch, (item, _) =>
        {
            if (item.Offset == 11) throw new IOException("MongoDB is down");
            handled.Add(item.Offset);
            return Task.CompletedTask;
        }, CancellationToken.None));

        Assert.Contains(10L, handled);
        Assert.DoesNotContain(12L, handled);
    }

    [Fact]
    public void CommitOffsetsAreOnePastTheHighestPerPartition()
    {
        Assert.Equal(new Dictionary<int, long> { [0] = 13, [1] = 22 }, KeyedBatchProcessor.CommitOffsets(Batch));
    }

    [Fact]
    public void RewindOffsetsAreTheLowestPerPartition()
    {
        Assert.Equal(new Dictionary<int, long> { [0] = 10, [1] = 20 }, KeyedBatchProcessor.RewindOffsets(Batch));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}

public class MessageFailurePolicyTests
{
    private readonly List<TimeSpan> _waits = new();
    private readonly List<string> _deadLettered = new();
    private MessageFailurePolicy Policy() => new((delay, _) => { _waits.Add(delay); return Task.CompletedTask; });
    private static readonly BatchItem<string> Item = new("ada", 0, 10, "message");

    private Task DeadLetter(BatchItem<string> item, UnprocessableMessageException ex, CancellationToken _)
    {
        _deadLettered.Add(ex.Reason);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ATemporaryFailureIsRetriedAfter200msThen1sThen5s()
    {
        int calls = 0;

        await Policy().HandleAsync(Item, (_, _) => ++calls < 4 ? throw new TimeoutException() : Task.CompletedTask,
            DeadLetter, null, CancellationToken.None);

        Assert.Equal(4, calls);
        Assert.Equal([TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)], _waits);
    }

    // After the three retries the batch fails, so it is rewound and read again later.
    [Fact]
    public async Task AFailureThatOutlastsTheRetriesIsRaised()
    {
        await Assert.ThrowsAsync<TimeoutException>(() => Policy().HandleAsync(
            Item, (_, _) => throw new TimeoutException(), DeadLetter, null, CancellationToken.None));

        Assert.Equal(3, _waits.Count);
        Assert.Empty(_deadLettered);
    }

    [Fact]
    public async Task AnUnprocessableMessageIsDeadLetteredAtOnceWithoutRetries()
    {
        await Policy().HandleAsync(Item, (_, _) => throw new UnprocessableMessageException("body_not_json"),
            DeadLetter, null, CancellationToken.None);

        Assert.Equal(["body_not_json"], _deadLettered);
        Assert.Empty(_waits);
    }

    // The DLQ being unreachable must not lose the message: it counts as temporary.
    [Fact]
    public async Task FailingToDeadLetterIsRetriedLikeAnyTemporaryFailure()
    {
        int dlqCalls = 0;

        await Policy().HandleAsync(Item, (_, _) => throw new UnprocessableMessageException("body_not_json"),
            (_, _, _) => ++dlqCalls < 2 ? throw new IOException("Kafka is down") : Task.CompletedTask,
            null, CancellationToken.None);

        Assert.Equal(2, dlqCalls);
        Assert.Equal([TimeSpan.FromMilliseconds(200)], _waits);
    }

    [Fact]
    public async Task ShutdownIsNotRetried()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(() => Policy().HandleAsync(
            Item, (_, _) => throw new OperationCanceledException(), DeadLetter, null, CancellationToken.None));

        Assert.Empty(_waits);
    }
}
