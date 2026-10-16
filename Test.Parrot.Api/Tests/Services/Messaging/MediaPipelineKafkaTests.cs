using System.Collections.Concurrent;
using System.Text;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;
using Parrot.Domain.Entities.Messages;
using Parrot.Domain.Enums;
using Parrot.Infrastructure.MessageBus;
using Parrot.Infrastructure.Messaging;

namespace Test.Parrot.Api.Tests.Services.Messaging;

/// <summary>
/// The media and send pipelines end to end on a real broker and a real database, with failures injected: duplicate
/// deliveries, a poison job, a job that keeps failing, a database write that fails once, a channel that drops.
/// Needs PARROT_TEST_KAFKA (e.g. localhost:29092) and PARROT_TEST_MONGO; it makes and deletes topics of its own.
/// </summary>
public class MediaPipelineKafkaTests : IAsyncLifetime
{
    public sealed class LivePipelineFactAttribute : FactAttribute
    {
        public LivePipelineFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("PARROT_TEST_KAFKA") is null || Environment.GetEnvironmentVariable("PARROT_TEST_MONGO") is null)
                Skip = "Set PARROT_TEST_KAFKA and PARROT_TEST_MONGO to a disposable local broker and database.";
        }
    }

    private static readonly Guid Connection = Guid.Parse("6f1c2d3e-4a5b-4c6d-8e7f-0a1b2c3d4e5f");
    private readonly string _servers = Environment.GetEnvironmentVariable("PARROT_TEST_KAFKA") ?? "";
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];
    private readonly string _database = "parrot_test_" + Guid.NewGuid().ToString("N");
    private readonly ConcurrentQueue<MessageSettled> _settled = new();
    private KafkaSettings _settings = new();
    private KafkaMessageBus? _bus;
    private MongoClient? _mongo;

    private string[] Topics => [_settings.MediaTopic, _settings.MediaDeadLetterTopic, _settings.OutboundTopic, _settings.OutboundDeadLetterTopic, _settings.SettledTopic];

    public async Task InitializeAsync()
    {
        if (_servers.Length == 0 || Environment.GetEnvironmentVariable("PARROT_TEST_MONGO") is not { } mongo) return;
        _mongo = new MongoClient(mongo);
        MessageSerializationRegistry.Register();
        _settings = new KafkaSettings
        {
            BootstrapServers = _servers,
            MediaTopic = $"test.media.{_suffix}", MediaDeadLetterTopic = $"test.media.{_suffix}.dlq",
            OutboundTopic = $"test.outbound.{_suffix}", OutboundDeadLetterTopic = $"test.outbound.{_suffix}.dlq",
            SettledTopic = $"test.settled.{_suffix}",
        };
        using IAdminClient admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = _servers }).Build();
        await admin.CreateTopicsAsync(Topics.Select(t => new TopicSpecification { Name = t, NumPartitions = t.EndsWith(".dlq") ? 1 : 3, ReplicationFactor = 1 }));
        _bus = new KafkaMessageBus(Options.Create(_settings), NullLogger<KafkaMessageBus>.Instance);
    }

    public async Task DisposeAsync()
    {
        if (_mongo is not null) await _mongo.DropDatabaseAsync(_database);
        if (_bus is null) return;
        _bus.Dispose();
        using IAdminClient admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = _servers }).Build();
        await admin.DeleteTopicsAsync(Topics);
    }

    private IOptions<MongoDbSettings> MongoSettings => Options.Create(new MongoDbSettings { Database = _database, Collection = "messages" });

    private IMongoCollection<MessageDocument> Messages => _mongo!.GetDatabase(_database).GetCollection<MessageDocument>("messages");

    private MongoStoredMessageStore Store() => new(_mongo!, MongoSettings);

    private static async Task Until(Func<Task<bool>> condition, int seconds = 45)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline && !await condition()) await Task.Delay(150);
        Assert.True(await condition(), "Timed out waiting for the pipeline.");
    }

    // Everything the settled topic carries, read from the start by a group of its own.
    private Task StartSettledListener(CancellationToken stop) =>
        _bus!.SubscribeAsync<MessageSettled>(_settings.SettledTopic, (m, _) => { _settled.Enqueue(m); return Task.CompletedTask; },
            new SubscriptionOptions(GroupId: "settled-" + _suffix, StartFromLatest: false), stop);

    private List<(string Reason, string Key)> ReadDeadLetters(string topic, int expected, int seconds = 15)
    {
        using IConsumer<string, string> consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = _servers, GroupId = "dlq-reader-" + Guid.NewGuid().ToString("N"), AutoOffsetReset = AutoOffsetReset.Earliest,
        }).Build();
        consumer.Subscribe(topic);
        List<(string, string)> found = [];
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (found.Count < expected && DateTime.UtcNow < deadline)
        {
            ConsumeResult<string, string>? result = consumer.Consume(TimeSpan.FromSeconds(1));
            if (result is not null) found.Add((Encoding.UTF8.GetString(result.Message.Headers.GetLastBytes("dlq.reason")), result.Message.Key));
        }

        return found;
    }

    private static MessageDocument Incoming(string id) => new WhatsAppMessageDocument
    {
        MessageId = id, OwnerUserId = "user-1", ConnectionId = Connection, Platform = MessagingPlatform.WhatsApp, ExternalAccountId = "waba",
        ChannelId = "phone", ChannelDisplayName = "", From = "2348012345678", Timestamp = "1", Type = "image", StoredAt = DateTime.UtcNow,
        Attachments = [new MessageAttachment { Kind = "image", Status = MediaStatus.Pending, ProviderMediaId = "media-1" }],
    };

    private static MessageDocument Reply(string id) => new WhatsAppMessageDocument
    {
        MessageId = id, OwnerUserId = "user-1", ConnectionId = Connection, Platform = MessagingPlatform.WhatsApp, ExternalAccountId = "waba",
        ChannelId = "phone", ChannelDisplayName = "", From = "2348012345678", Timestamp = "1", Type = "image", Outgoing = true, SendStatus = "pending",
        StoredAt = DateTime.UtcNow, Attachments = [new MessageAttachment { Kind = "image", Status = MediaStatus.Ready, StorageKey = "business-media/k.png" }],
    };

    // ---- inbound media ----

    private sealed class Downloader : IInboundMediaStore
    {
        public ConcurrentDictionary<string, int> Calls { get; } = new();

        public Task<IReadOnlyList<IncomingMedia>> SaveAsync(IncomingMessage message, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IncomingMedia> SaveFileAsync(IncomingMessage message, int index, CancellationToken cancellationToken)
        {
            Calls.AddOrUpdate(message.MessageId, 1, (_, n) => n + 1);
            return Task.FromResult(message.MessageId.StartsWith("bad", StringComparison.Ordinal)
                ? new IncomingMedia { Kind = "image", Status = MediaStatus.Failed, FailureReason = MediaFailure.Unavailable }
                : new IncomingMedia { Kind = "image", Status = MediaStatus.Ready, StorageKey = "customer-media/u/" + message.MessageId, ContentType = "image/jpeg", SizeBytes = 3 });
        }
    }

    // The first write of one message's outcome fails, as a database blip would.
    private sealed class FlakyStore(IStoredMessageStore inner, string failOnce) : IStoredMessageStore
    {
        private int _failed;

        public Task<MessageDocument?> FindAsync(string id, CancellationToken ct = default) => inner.FindAsync(id, ct);
        public Task<MessageDocument> InsertOrGetAsync(MessageDocument d, CancellationToken ct = default) => inner.InsertOrGetAsync(d, ct);

        public Task<bool> CompleteMediaAsync(string messageId, int index, IncomingMedia outcome, CancellationToken ct = default)
        {
            if (messageId == failOnce && Interlocked.Exchange(ref _failed, 1) == 0) throw new TimeoutException("MongoDB blipped");
            return inner.CompleteMediaAsync(messageId, index, outcome, ct);
        }

        public Task<bool> ClaimSendAsync(string id, DateTime at, CancellationToken ct = default) => inner.ClaimSendAsync(id, at, ct);
        public Task<bool> CompleteSendAsync(string id, string s, string? p, CancellationToken ct = default) => inner.CompleteSendAsync(id, s, p, ct);
        public Task<IReadOnlyList<StaleMessage>> FindStalePendingMediaAsync(DateTime b, int l, CancellationToken ct = default) => inner.FindStalePendingMediaAsync(b, l, ct);
        public Task<IReadOnlyList<StaleMessage>> FindStalePendingSendsAsync(DateTime b, DateTime c, int l, CancellationToken ct = default) => inner.FindStalePendingSendsAsync(b, c, l, ct);
        public Task<bool> FailPendingSendAsync(string id, CancellationToken ct = default) => inner.FailPendingSendAsync(id, ct);
    }

    [LivePipelineFact]
    public async Task Media_jobs_over_kafka_settle_once_each_survive_a_database_blip_and_dead_letter_the_poison_and_the_exhausted()
    {
        await Messages.InsertManyAsync([Incoming("ok-1"), Incoming("ok-2"), Incoming("ok-3"), Incoming("bad-1"), Incoming("flaky-1")]);
        Downloader downloader = new();
        MediaJobProcessor processor = new(
            new FlakyStore(Store(), failOnce: "flaky-1"), downloader, NullLogger<MediaJobProcessor>.Instance,
            new KafkaMessageSettledPublisher(_bus!, Options.Create(_settings), NullLogger<KafkaMessageSettledPublisher>.Instance),
            delay: (_, _) => Task.CompletedTask);
        KafkaMediaJobPublisher jobs = new(_bus!, Options.Create(_settings));
        using CancellationTokenSource stop = new();
        Task settledListener = StartSettledListener(stop.Token);
        Task worker = _bus!.SubscribeKeyedAsync<MediaJob>(_settings.MediaTopic, processor.ProcessAsync,
            new KeyedSubscriptionOptions { GroupId = "media-" + _suffix, DeadLetterTopic = _settings.MediaDeadLetterTopic, MaxParallelism = 8, MaxBatchSize = 32 }, stop.Token);

        // Each good file's job three times over (a replay delivers twice at least), the failing one, the flaky one, and a job for nothing.
        MediaJob Job(string id) => new() { MessageId = id, Index = 0, CorrelationId = "t", EnqueuedAt = DateTime.UtcNow };
        foreach (string id in new[] { "ok-1", "ok-2", "ok-3" })
            for (int i = 0; i < 3; i++) await jobs.PublishAsync(Job(id));
        await jobs.PublishAsync(Job("bad-1"));
        await jobs.PublishAsync(Job("flaky-1"));
        await jobs.PublishAsync(Job("ghost"));

        await Until(async () =>
        {
            MessageDocument[] docs = await Messages.Find(Builders<MessageDocument>.Filter.Empty).ToListAsync().ContinueWith(t => t.Result.ToArray());
            return docs.All(d => d.Attachments![0].Status != MediaStatus.Pending);
        });
        await Task.Delay(3000);   // long enough for any duplicate to have been announced
        await stop.CancelAsync();
        await Task.WhenAll(worker, settledListener);

        Assert.Equal(MediaStatus.Ready, (await Store().FindAsync("ok-1"))!.Attachments![0].Status);
        Assert.Equal("customer-media/u/ok-2", (await Store().FindAsync("ok-2"))!.Attachments![0].StorageKey);
        Assert.Equal(MediaStatus.Ready, (await Store().FindAsync("flaky-1"))!.Attachments![0].Status);
        MessageAttachment bad = (await Store().FindAsync("bad-1"))!.Attachments![0];
        Assert.Equal((MediaStatus.Failed, MediaFailure.Unavailable), (bad.Status, bad.FailureReason));

        // Announced exactly once per file, however many jobs were delivered.
        Assert.Equal(["bad-1", "flaky-1", "ok-1", "ok-2", "ok-3"], _settled.Select(s => s.MessageId).Order());
        Assert.Equal("failed", _settled.Single(s => s.MessageId == "bad-1").Status);

        // The poison job and the exhausted one, and nothing else, were dead-lettered with their reasons.
        List<(string Reason, string Key)> dead = ReadDeadLetters(_settings.MediaDeadLetterTopic, expected: 3, seconds: 8);
        Assert.Equal(
            [("media_message_not_found", "ghost"), ("media_retries_exhausted:unavailable", "bad-1")],
            dead.OrderBy(d => d.Key == "ghost" ? 0 : 1));
    }

    // ---- outbound sends ----

    private sealed class Channel : IOutboundFileDispatcher
    {
        public ConcurrentDictionary<string, int> Calls { get; } = new();

        public async Task<FileDispatchResult> DispatchAsync(MessageDocument reply, CancellationToken cancellationToken)
        {
            Calls.AddOrUpdate(reply.MessageId, 1, (_, n) => n + 1);
            await Task.Delay(Random.Shared.Next(1, 20), cancellationToken);
            return (int.Parse(reply.MessageId.Split('-')[^1]) % 4) switch
            {
                0 or 3 => new FileDispatchResult("wamid." + reply.MessageId, null),
                1 => new FileDispatchResult(null, "131047"),
                _ => throw new HttpRequestException("connection reset"),
            };
        }
    }

    [LivePipelineFact]
    public async Task Sends_over_kafka_reach_the_channel_exactly_once_each_however_many_times_their_jobs_are_delivered()
    {
        const int replies = 20, duplicates = 3;
        await Messages.InsertManyAsync(Enumerable.Range(0, replies).Select(n => Reply($"reply-{n}")));
        Channel channel = new();
        OutboundFileProcessor processor = new(
            Store(), channel, NullLogger<OutboundFileProcessor>.Instance,
            new KafkaMessageSettledPublisher(_bus!, Options.Create(_settings), NullLogger<KafkaMessageSettledPublisher>.Instance),
            delay: (_, _) => Task.CompletedTask);
        KafkaOutboundJobPublisher jobs = new(_bus!, Options.Create(_settings));
        using CancellationTokenSource stop = new();
        Task settledListener = StartSettledListener(stop.Token);
        Task worker = _bus!.SubscribeKeyedAsync<OutboundJob>(_settings.OutboundTopic, processor.ProcessAsync,
            new KeyedSubscriptionOptions { GroupId = "outbound-" + _suffix, DeadLetterTopic = _settings.OutboundDeadLetterTopic, MaxParallelism = 8, MaxBatchSize = 16 }, stop.Token);

        for (int d = 0; d < duplicates; d++)
            for (int n = 0; n < replies; n++)
                await jobs.PublishAsync(new OutboundJob { MessageId = $"reply-{n}", ThreadKey = OutboundJob.KeyFor(Connection, "2348012345678"), EnqueuedAt = DateTime.UtcNow });

        await Until(async () => (await Messages.Find(m => m.SendStatus == "pending" || m.SendStatus == "sending").CountDocumentsAsync()) == 0);
        await Task.Delay(3000);
        await stop.CancelAsync();
        await Task.WhenAll(worker, settledListener);

        for (int n = 0; n < replies; n++)
        {
            Assert.Equal(1, channel.Calls[$"reply-{n}"]);
            Assert.Equal((n % 4) switch { 0 or 3 => "accepted", 1 => "failed", _ => "unknown" }, (await Store().FindAsync($"reply-{n}"))!.SendStatus);
        }

        Assert.Equal(replies, _settled.Count);
        Assert.Equal(replies, _settled.Select(s => s.MessageId).Distinct().Count());
        Assert.All(_settled, s => Assert.Equal(SettledKinds.Send, s.Kind));
        Assert.Empty(ReadDeadLetters(_settings.OutboundDeadLetterTopic, expected: 1, seconds: 3));
    }
}
