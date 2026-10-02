using System.Collections.Concurrent;
using System.Text;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;
using Parrot.Infrastructure.MessageBus;

namespace Test.Parrot.Api.Tests.Services.Messaging;

/// <summary>Against a real broker: PARROT_TEST_KAFKA=localhost:29092 with docker-compose.local.yml up.</summary>
public class KafkaKeyedSubscriptionTests : IAsyncLifetime
{
    public sealed class KafkaFactAttribute : FactAttribute
    {
        public KafkaFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("PARROT_TEST_KAFKA") is null)
                Skip = "Set PARROT_TEST_KAFKA to a disposable local Kafka, e.g. localhost:29092 from docker-compose.local.yml.";
        }
    }

    public sealed record TestMessage(string Conversation, int N);

    private readonly string _servers = Environment.GetEnvironmentVariable("PARROT_TEST_KAFKA") ?? "";
    private readonly string _topic = "test.keyed." + Guid.NewGuid().ToString("N")[..8];
    private string Dlq => _topic + ".dlq";
    private readonly string _group = "test-group-" + Guid.NewGuid().ToString("N")[..8];
    private KafkaMessageBus? _bus;

    public async Task InitializeAsync()
    {
        if (_servers.Length == 0) return;
        using IAdminClient admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = _servers }).Build();
        await admin.CreateTopicsAsync(
        [
            new TopicSpecification { Name = _topic, NumPartitions = 3, ReplicationFactor = 1 },
            new TopicSpecification { Name = Dlq, NumPartitions = 1, ReplicationFactor = 1 },
        ]);
        _bus = new KafkaMessageBus(Options.Create(new KafkaSettings { BootstrapServers = _servers }), NullLogger<KafkaMessageBus>.Instance);
    }

    public async Task DisposeAsync()
    {
        if (_servers.Length == 0) return;
        _bus?.Dispose();
        using IAdminClient admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = _servers }).Build();
        await admin.DeleteTopicsAsync([_topic, Dlq]);
    }

    private KeyedSubscriptionOptions Options_ => new() { DeadLetterTopic = Dlq, GroupId = _group };

    private Task Publish(string conversation, int n) => _bus!.PublishAsync(_topic, conversation, new TestMessage(conversation, n));

    private async Task PublishRaw(string key, string value)
    {
        using IProducer<string, string> producer = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = _servers }).Build();
        await producer.ProduceAsync(_topic, new Message<string, string> { Key = key, Value = value });
    }

    private static async Task Until(Func<bool> condition, int seconds = 30)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(100);
        Assert.True(condition(), "Timed out waiting for the consumer.");
    }

    [KafkaFact]
    public async Task OrderIsKeptPerConversation_AndCommittedMessagesAreNotReadAgain()
    {
        for (int n = 1; n <= 5; n++)
        {
            await Publish("ada", n);
            await Publish("tunde", n);
        }

        ConcurrentDictionary<string, ConcurrentQueue<int>> seen = new();
        using (CancellationTokenSource stop = new())
        {
            Task run = _bus!.SubscribeKeyedAsync<TestMessage>(_topic, async (m, _) =>
            {
                await Task.Delay(Random.Shared.Next(1, 10));
                seen.GetOrAdd(m.Conversation, _ => new()).Enqueue(m.N);
            }, Options_, stop.Token);

            await Until(() => seen.Values.Sum(q => q.Count) == 10);
            await stop.CancelAsync();
            await run;
        }

        Assert.Equal([1, 2, 3, 4, 5], seen["ada"]);
        Assert.Equal([1, 2, 3, 4, 5], seen["tunde"]);

        // Same group again: everything was committed, so nothing comes back.
        int redelivered = 0;
        using CancellationTokenSource again = new(TimeSpan.FromSeconds(8));
        await _bus!.SubscribeKeyedAsync<TestMessage>(_topic, (_, _) => { Interlocked.Increment(ref redelivered); return Task.CompletedTask; }, Options_, again.Token);
        Assert.Equal(0, redelivered);
    }

    [KafkaFact]
    public async Task AnUnreadableMessageIsDeadLetteredWithItsReason_AndTheNextOneStillRuns()
    {
        await PublishRaw("ada", "not json");
        await Publish("ada", 2);

        ConcurrentQueue<int> handled = new();
        using CancellationTokenSource stop = new();
        Task run = _bus!.SubscribeKeyedAsync<TestMessage>(_topic, (m, _) => { handled.Enqueue(m.N); return Task.CompletedTask; }, Options_, stop.Token);
        await Until(() => handled.Contains(2));
        await stop.CancelAsync();
        await run;

        using IConsumer<string, string> dlq = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = _servers, GroupId = "dlq-reader-" + Guid.NewGuid().ToString("N"), AutoOffsetReset = AutoOffsetReset.Earliest,
        }).Build();
        dlq.Subscribe(Dlq);
        ConsumeResult<string, string> dead = dlq.Consume(TimeSpan.FromSeconds(15));

        Assert.NotNull(dead);
        Assert.Equal("ada", dead.Message.Key);
        Assert.Equal("not json", dead.Message.Value);
        Assert.Equal("payload_not_deserializable", Encoding.UTF8.GetString(dead.Message.Headers.GetLastBytes("dlq.reason")));
        Assert.Equal(_topic, Encoding.UTF8.GetString(dead.Message.Headers.GetLastBytes("dlq.source.topic")));
    }

    // MongoDB down for longer than the retries: the batch is rewound and read
    // again, and the message is handled once it recovers, still in order.
    [KafkaFact]
    public async Task AFailureThatOutlastsTheRetriesRewindsTheBatchAndSucceedsLater()
    {
        await Publish("ada", 1);
        await Publish("ada", 2);

        int failuresLeft = 4; // more than the three retries
        ConcurrentQueue<int> succeeded = new();
        using CancellationTokenSource stop = new();
        Task run = _bus!.SubscribeKeyedAsync<TestMessage>(_topic, (m, _) =>
        {
            if (m.N == 1 && Interlocked.Decrement(ref failuresLeft) >= 0) throw new TimeoutException("MongoDB is down");
            succeeded.Enqueue(m.N);
            return Task.CompletedTask;
        }, Options_, stop.Token);

        await Until(() => succeeded.Contains(2), seconds: 45);
        await stop.CancelAsync();
        await run;

        Assert.Equal([1, 2], succeeded);
    }

    [KafkaFact]
    public async Task OneConversationFailingDoesNotHoldUpAnotherInTheSameBatch()
    {
        await Publish("ada", 1);
        await Publish("tunde", 1);

        ConcurrentQueue<string> succeeded = new();
        using CancellationTokenSource stop = new();
        Task run = _bus!.SubscribeKeyedAsync<TestMessage>(_topic, (m, _) =>
        {
            if (m.Conversation == "ada") throw new TimeoutException("still down");
            succeeded.Enqueue(m.Conversation);
            return Task.CompletedTask;
        }, Options_, stop.Token);

        await Until(() => succeeded.Contains("tunde"));
        await stop.CancelAsync();
        await run;
    }
}
