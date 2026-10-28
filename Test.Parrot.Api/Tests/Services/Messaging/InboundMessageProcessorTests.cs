using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Parrot.Application.Agents;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;
using Parrot.Domain.Entities.Messages;
using Parrot.Domain.Enums;
using Parrot.Infrastructure.Messaging;
using static Test.Parrot.Api.Tests.Services.MongoConversationTests;

namespace Test.Parrot.Api.Tests.Services.Messaging;

public class InboundMessageProcessorTests
{
    private const string Key = "whatsapp:2348012345678:2349011112222";
    private static readonly Guid Connection = Guid.Parse("6f1c2d3e-4a5b-4c6d-8e7f-0a1b2c3d4e5f");

    private const string Body = """
        {"object":"whatsapp_business_account","entry":[{"id":"waba-1","changes":[{"field":"messages","value":{
          "messaging_product":"whatsapp",
          "metadata":{"display_phone_number":"+234 901 111 2222","phone_number_id":"phone-1"},
          "contacts":[{"wa_id":"2348012345678","profile":{"name":"Ada"}}],
          "messages":[{"from":"2348012345678","id":"wamid.A","timestamp":"1790589123","type":"text","text":{"body":"Hello"}}]}}]}]}
        """;

    private sealed class FakeStore : IStoredMessageStore
    {
        public List<MessageDocument> Documents { get; } = new();
        public Exception? Failure { get; set; }

        public Task<MessageDocument?> FindAsync(string messageId, CancellationToken cancellationToken = default)
        {
            if (Failure is not null) throw Failure;
            return Task.FromResult(Documents.FirstOrDefault(d => d.MessageId == messageId));
        }

        public Task<MessageDocument?> FindByProviderIdAsync(Guid connectionId, string providerMessageId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Documents.FirstOrDefault(d => d.ProviderMessageId == providerMessageId && d.ConnectionId == connectionId));

        public Task<MessageDocument> InsertOrGetAsync(MessageDocument document, CancellationToken cancellationToken = default)
        {
            if (Failure is not null) throw Failure;
            MessageDocument? existing = Documents.FirstOrDefault(d => d.MessageId == document.MessageId);
            if (existing is not null) return Task.FromResult(existing);
            Documents.Add(document);
            return Task.FromResult(document);
        }

        public Task<bool> CompleteMediaAsync(string messageId, int index, IncomingMedia outcome, CancellationToken cancellationToken = default) =>
            Task.FromResult(MediaTestStore.Complete(Documents.FirstOrDefault(d => d.MessageId == messageId), index, outcome));

        public Task<bool> ClaimSendAsync(string messageId, DateTime claimedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> CompleteSendAsync(string messageId, string status, string? providerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<StaleMessage>> FindStalePendingMediaAsync(DateTime storedBefore, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<StaleMessage>> FindStalePendingSendsAsync(DateTime storedBefore, DateTime claimedBefore, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> FailPendingSendAsync(string messageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeSequence : IConversationSequence
    {
        private readonly Dictionary<string, long> _next = new();
        public int Taken { get; private set; }

        public Task<long> NextAsync(string conversationKey, CancellationToken cancellationToken = default)
        {
            Taken++;
            _next[conversationKey] = _next.GetValueOrDefault(conversationKey) + 1;
            return Task.FromResult(_next[conversationKey]);
        }
    }

    private sealed class FakePublisher : IMessagePersistedPublisher
    {
        public List<MessagePersisted> Published { get; } = new();
        public int FailuresLeft { get; set; }

        public Task PublishAsync(MessagePersisted persisted, CancellationToken cancellationToken = default)
        {
            if (FailuresLeft-- > 0) throw new IOException("Kafka is down");
            Published.Add(persisted);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeReceipts : IConnectionMessageReceipt
    {
        public int Recorded { get; private set; }
        public int FailuresLeft { get; set; }

        public Task RecordAsync(IncomingMessage message, CancellationToken cancellationToken)
        {
            if (FailuresLeft-- > 0) throw new InvalidOperationException("PostgreSQL is down");
            Recorded++;
            return Task.CompletedTask;
        }
    }

    // Keeps each line with the scope values in force when it was written, as a log shipper sees them.
    private sealed class CapturingLogger : ILogger<InboundMessageProcessor>
    {
        private readonly Stack<IReadOnlyDictionary<string, object?>> _scopes = new();

        public List<(LogLevel Level, string Text, IReadOnlyDictionary<string, object?> Scope)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            _scopes.Push(state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.ToDictionary(p => p.Key, p => p.Value)
                : new Dictionary<string, object?>());
            return new Pop(_scopes);
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), _scopes.Count > 0 ? _scopes.Peek() : new Dictionary<string, object?>()));

        public IEnumerable<string> Texts => Entries.Select(e => e.Text);

        private sealed class Pop(Stack<IReadOnlyDictionary<string, object?>> scopes) : IDisposable
        {
            public void Dispose() => scopes.Pop();
        }
    }

    private readonly FakeStore _store = new();
    private readonly FakeSequence _sequence = new();
    private readonly FakePublisher _publisher = new();
    private readonly FakeReceipts _receipts = new();

    private sealed class FakeContactNames(string? name) : IContactNameLookup
    {
        public List<string> AskedFor { get; } = new();

        public Task<ContactProfile?> FindAsync(IncomingMessage message, CancellationToken cancellationToken)
        {
            AskedFor.Add(message.From);
            return Task.FromResult(name is null ? null : new ContactProfile(name, null));
        }
    }

    private sealed class FakeMedia : IInboundMediaStore
    {
        public int Saved { get; private set; }

        public Task<IReadOnlyList<IncomingMedia>> SaveAsync(IncomingMessage message, CancellationToken cancellationToken)
        {
            Saved++;
            return Task.FromResult<IReadOnlyList<IncomingMedia>>([.. message.Media!.Select(m => m with { StorageKey = "customer-media/user-1/copy-0.jpg", SizeBytes = 3 })]);
        }

        public Task<IncomingMedia> SaveFileAsync(IncomingMessage message, int index, CancellationToken cancellationToken)
        {
            Saved++;
            return Task.FromResult(message.Media![index] with { StorageKey = "customer-media/user-1/copy-0.jpg", SizeBytes = 3 });
        }
    }

    private sealed class FakeJobs : IMediaJobPublisher
    {
        public List<MediaJob> Published { get; } = new();
        public int FailuresLeft { get; set; }

        public Task PublishAsync(MediaJob job, CancellationToken cancellationToken = default)
        {
            if (FailuresLeft-- > 0) throw new IOException("Kafka is down");
            Published.Add(job);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAgentChannelLookup(bool isLive) : IAgentChannelLookup
    {
        public Task<bool> IsAgentLiveAsync(string workspaceId, string channel, CancellationToken ct) =>
            Task.FromResult(isLive);
    }

    private InboundMessageProcessor Processor(
        IStoredMessageStore? store = null, ILogger<InboundMessageProcessor>? logger = null, IContactNameLookup? contactNames = null,
        IInboundMediaStore? media = null, IMediaJobPublisher? jobs = null, bool deferMedia = false,
        IAgentChannelLookup? agentChannelLookup = null) => new(
        new InboundMessageDecoders([new WhatsAppInboundDecoder(), new FacebookInboundDecoder()]),
        store ?? _store, _sequence,
        new MessageDocumentMapperFactory([new WhatsAppMessageDocumentMapper(), new FacebookMessageDocumentMapper(), new TikTokMessageDocumentMapper(), new InstagramMessageDocumentMapper()]),
        _receipts, _publisher, logger ?? NullLogger<InboundMessageProcessor>.Instance, contactNames, media, jobs, deferMedia,
        customerRepository: null, agentChannelLookup: agentChannelLookup);

    private const string Psid = "6215837091842310";

    private const string MessengerBody = $$$"""
        {"object":"page","entry":[{"id":"185860297939218","time":1791447800000,"messaging":[
          {"sender":{"id":"{{{Psid}}}"},"recipient":{"id":"185860297939218"},"timestamp":1791447800000,
           "message":{"mid":"m_1","text":"Hello"}}]}]}
        """;

    private static InboundMessage MessengerInbound() => Inbound("facebook:m_1", MessengerBody) with
    {
        Source = MessagingPlatform.Facebook, Sender = Psid, ConversationKey = "facebook:" + Psid + ":185860297939218",
    };

    private static InboundMessage Inbound(string messageId = "whatsapp:wamid.A", string body = Body, string? owner = "user-1") => new()
    {
        MessageId = messageId,
        Source = MessagingPlatform.WhatsApp,
        Sender = "2348012345678",
        Receiver = "2349011112222",
        ConversationKey = Key,
        Data = body,
        ReceivedAt = new DateTime(2026, 10, 2, 9, 30, 1, DateTimeKind.Utc),
        CorrelationId = "487b7279745d4c41675818ffee460c54",
        ConnectionId = Connection,
        OwnerUserId = owner,
    };

    [Fact]
    public async Task ANewMessageIsStoredWithItsSequenceThenAnnounced()
    {
        await Processor().ProcessAsync(Inbound(), CancellationToken.None);

        MessageDocument stored = Assert.Single(_store.Documents);
        Assert.Equal("whatsapp:wamid.A", stored.MessageId);
        Assert.Equal(Key, stored.ConversationKey);
        Assert.Equal(1, stored.Sequence);
        Assert.Equal("2349011112222", stored.Receiver);
        Assert.Equal("user-1", stored.OwnerUserId);

        MessagePersisted published = Assert.Single(_publisher.Published);
        Assert.Equal(MessagingPlatform.WhatsApp, published.Source);
        Assert.Equal("487b7279745d4c41675818ffee460c54", published.CorrelationId);
        Assert.Equal("whatsapp:wamid.A", published.Data.MessageId);
        Assert.Equal(1, published.Data.Sequence);
        Assert.Equal(Key, published.Data.ConversationKey);
        Assert.Equal("user-1", published.Data.OwnerUserId);
        Assert.Equal("2348012345678", published.Data.Sender);
        Assert.Equal("2349011112222", published.Data.Receiver);
        Assert.Equal("Ada", published.Data.SenderName);
        Assert.Equal("Hello", published.Data.Text);
        Assert.False(published.Data.Outgoing);
        Assert.Equal(1, _receipts.Recorded);
    }

    private const string ImageBody = """
        {"object":"whatsapp_business_account","entry":[{"id":"waba-1","changes":[{"field":"messages","value":{
          "messaging_product":"whatsapp",
          "metadata":{"display_phone_number":"+234 901 111 2222","phone_number_id":"phone-1"},
          "messages":[{"from":"2348012345678","id":"wamid.IMG","timestamp":"1790589123","type":"image","image":{"id":"media-1","mime_type":"image/jpeg"}}]}}]}]}
        """;

    [Fact]
    public async Task AMessageWithAPictureIsStoredAndAnnouncedWithTheCopyOfIt_AndAReplayDoesNotDownloadAgain()
    {
        FakeMedia media = new();
        InboundMessage inbound = Inbound("whatsapp:wamid.IMG", ImageBody);

        await Processor(media: media).ProcessAsync(inbound, CancellationToken.None);
        await Processor(media: media).ProcessAsync(inbound, CancellationToken.None);

        MessageAttachment saved = Assert.Single(Assert.Single(_store.Documents).Attachments!);
        Assert.Equal("customer-media/user-1/copy-0.jpg", saved.StorageKey);
        Assert.Equal("media-1", saved.ProviderMediaId);
        Assert.Equal(1, media.Saved);
        Assert.All(_publisher.Published, p => Assert.True(Assert.Single(p.Data.Attachments!).Available));
    }

    [Fact]
    public async Task WhenDeferredAPictureIsStoredPendingWithoutBeingDownloaded_AndOneJobIsQueuedForIt()
    {
        FakeMedia media = new();
        FakeJobs jobs = new();

        await Processor(media: media, jobs: jobs, deferMedia: true).ProcessAsync(Inbound("whatsapp:wamid.IMG", ImageBody), CancellationToken.None);

        MessageAttachment stored = Assert.Single(Assert.Single(_store.Documents).Attachments!);
        Assert.Equal(MediaStatus.Pending, stored.Status);
        Assert.Null(stored.StorageKey);
        Assert.Equal("media-1", stored.ProviderMediaId);
        Assert.Equal(0, media.Saved);
        MediaJob job = Assert.Single(jobs.Published);
        Assert.Equal("whatsapp:wamid.IMG", job.MessageId);
        Assert.Equal(0, job.Index);
        Assert.Equal("487b7279745d4c41675818ffee460c54", job.CorrelationId);
        StoredAttachment announced = Assert.Single(Assert.Single(_publisher.Published).Data.Attachments!);
        Assert.Equal(MediaStatus.Pending, announced.Status);
        Assert.False(announced.Available);
    }

    [Fact]
    public async Task WhenDeferredAFailedJobPublishIsRetriedAndTheReplayQueuesTheFileAgain_WithoutStoringTwice()
    {
        FakeJobs jobs = new() { FailuresLeft = 1 };
        InboundMessage inbound = Inbound("whatsapp:wamid.IMG", ImageBody);

        await Assert.ThrowsAsync<IOException>(() => Processor(jobs: jobs, deferMedia: true).ProcessAsync(inbound, CancellationToken.None));
        Assert.Empty(_publisher.Published);

        await Processor(jobs: jobs, deferMedia: true).ProcessAsync(inbound, CancellationToken.None);

        Assert.Single(_store.Documents);
        Assert.Single(jobs.Published);
        Assert.Single(_publisher.Published);
    }

    [Fact]
    public async Task WithoutADeferredSettingTheFilesAreStillCopiedBeforeTheMessageIsStoredAndNoJobIsQueued()
    {
        FakeMedia media = new();
        FakeJobs jobs = new();

        await Processor(media: media, jobs: jobs).ProcessAsync(Inbound("whatsapp:wamid.IMG", ImageBody), CancellationToken.None);

        Assert.Equal(1, media.Saved);
        Assert.Empty(jobs.Published);
    }

    [Fact]
    public async Task AMessageWithAPictureIsStillStoredWhenNoMediaStoreIsWired()
    {
        await Processor().ProcessAsync(Inbound("whatsapp:wamid.IMG", ImageBody), CancellationToken.None);

        Assert.False(Assert.Single(Assert.Single(_publisher.Published).Data.Attachments!).Available);
    }

    // Messenger names the customer only by PSID; the name Meta gives for it is stored and announced.
    [Fact]
    public async Task AMessengerMessageIsStoredWithTheNameLookedUpForItsSender()
    {
        FakeContactNames names = new("Ada Obi");

        await Processor(contactNames: names).ProcessAsync(MessengerInbound(), CancellationToken.None);

        Assert.Equal([Psid], names.AskedFor);
        Assert.Equal("Ada Obi", Assert.Single(_store.Documents).ContactName);
        Assert.Equal("Ada Obi", Assert.Single(_publisher.Published).Data.SenderName);
    }

    [Fact]
    public async Task AMessengerMessageWithNoNameFoundIsStillStored()
    {
        await Processor(contactNames: new FakeContactNames(null)).ProcessAsync(MessengerInbound(), CancellationToken.None);

        Assert.Null(Assert.Single(_store.Documents).ContactName);
        Assert.Single(_publisher.Published);
    }

    // A redelivery finds the stored copy, so Meta is not asked again.
    [Fact]
    public async Task ADuplicateDeliveryDoesNotLookTheNameUpAgain()
    {
        FakeContactNames names = new("Ada Obi");

        await Processor(contactNames: names).ProcessAsync(MessengerInbound(), CancellationToken.None);
        await Processor(contactNames: names).ProcessAsync(MessengerInbound(), CancellationToken.None);

        Assert.Single(names.AskedFor);
    }

    // WhatsApp's webhook carries the name already.
    [Fact]
    public async Task AMessageThatCarriesItsNameIsNotLookedUp()
    {
        FakeContactNames names = new("Someone Else");

        await Processor(contactNames: names).ProcessAsync(Inbound(), CancellationToken.None);

        Assert.Empty(names.AskedFor);
        Assert.Equal("Ada", Assert.Single(_store.Documents).ContactName);
    }

    [Fact]
    public async Task ConsecutiveMessagesInAConversationTakeIncreasingSequences()
    {
        string twoMessages = Body.Replace(
            """{"from":"2348012345678","id":"wamid.A","timestamp":"1790589123","type":"text","text":{"body":"Hello"}}""",
            """{"from":"2348012345678","id":"wamid.A","timestamp":"1790589123","type":"text","text":{"body":"Hello"}},{"from":"2348012345678","id":"wamid.B","timestamp":"1790589124","type":"text","text":{"body":"Again"}}""");

        await Processor().ProcessAsync(Inbound("whatsapp:wamid.A", twoMessages), CancellationToken.None);
        await Processor().ProcessAsync(Inbound("whatsapp:wamid.B", twoMessages), CancellationToken.None);

        Assert.Equal([1L, 2L], _publisher.Published.Select(p => p.Data.Sequence));
    }

    // Meta sends a webhook twice, or Kafka redelivers: one document, one sequence, announced again.
    [Fact]
    public async Task ADuplicateDeliveryIsStoredOnceAndAnnouncedAgain()
    {
        await Processor().ProcessAsync(Inbound(), CancellationToken.None);
        await Processor().ProcessAsync(Inbound(), CancellationToken.None);

        Assert.Single(_store.Documents);
        Assert.Equal(1, _sequence.Taken);
        Assert.Equal(2, _publisher.Published.Count);
        Assert.All(_publisher.Published, p => Assert.Equal(1, p.Data.Sequence));
    }

    // Saved, then the publish failed (or the worker crashed before it): the
    // offset isn't committed, the message comes round again and is announced.
    [Fact]
    public async Task AReplayAfterAFailedPublishAnnouncesTheStoredMessage()
    {
        _publisher.FailuresLeft = 1;

        await Assert.ThrowsAsync<IOException>(() => Processor().ProcessAsync(Inbound(), CancellationToken.None));
        Assert.Single(_store.Documents);
        Assert.Empty(_publisher.Published);

        await Processor().ProcessAsync(Inbound(), CancellationToken.None);

        Assert.Single(_store.Documents);
        Assert.Equal(1, _sequence.Taken);
        Assert.Equal(1, Assert.Single(_publisher.Published).Data.Sequence);
    }

    // MongoDB down is temporary: the error reaches the bus, which retries, and nothing is announced.
    [Fact]
    public async Task MongoDbFailureLeavesTheMessageForRetry()
    {
        _store.Failure = new TimeoutException("MongoDB is down");

        await Assert.ThrowsAsync<TimeoutException>(() => Processor().ProcessAsync(Inbound(), CancellationToken.None));
        Assert.Empty(_publisher.Published);

        _store.Failure = null;
        await Processor().ProcessAsync(Inbound(), CancellationToken.None);

        Assert.Single(_store.Documents);
        Assert.Single(_publisher.Published);
    }

    [Fact]
    public async Task AReceiptFailureIsRetriedBeforeTheMessageIsAnnounced()
    {
        _receipts.FailuresLeft = 1;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Processor().ProcessAsync(Inbound(), CancellationToken.None));
        Assert.Empty(_publisher.Published);

        await Processor().ProcessAsync(Inbound(), CancellationToken.None);

        Assert.Single(_store.Documents);
        Assert.Single(_publisher.Published);
        Assert.Equal(1, _receipts.Recorded);
    }

    [Fact]
    public async Task AnUndecodableMessageIsUnprocessableAndNothingIsStored()
    {
        await Assert.ThrowsAsync<UnprocessableMessageException>(
            () => Processor().ProcessAsync(Inbound(body: "not json"), CancellationToken.None));

        Assert.Empty(_store.Documents);
        Assert.Equal(0, _sequence.Taken);
        Assert.Empty(_publisher.Published);
    }

    [Fact]
    public async Task AMessageIdAlreadyOwnedByAnotherBusinessIsUnprocessable()
    {
        await Processor().ProcessAsync(Inbound(owner: "business-a"), CancellationToken.None);

        UnprocessableMessageException ex = await Assert.ThrowsAsync<UnprocessableMessageException>(
            () => Processor().ProcessAsync(Inbound(owner: "business-b"), CancellationToken.None));

        Assert.Equal("message_id_owned_by_another_connection", ex.Reason);
        Assert.Single(_publisher.Published);
    }

    // The trail one message leaves: consumed, stored (MongoDB acknowledged), persisted (announced), in that order,
    // every line carrying the ids a search starts from.
    [Fact]
    public async Task ANewMessageLeavesAConsumedStoredPersistedTrailKeyedByItsIds()
    {
        CapturingLogger logger = new();

        await Processor(logger: logger).ProcessAsync(Inbound(), CancellationToken.None);

        string[] events = logger.Texts.Select(t => t.Split(' ')[0]).ToArray();
        Assert.Equal(["message.consumed", "message.stored", "message.persisted"], events);
        Assert.Contains("Outcome=stored Sequence=1 Type=text", logger.Entries[1].Text);
        Assert.All(logger.Entries, entry =>
        {
            Assert.Equal("whatsapp:wamid.A", entry.Scope["MessageId"]);
            Assert.Equal("487b7279745d4c41675818ffee460c54", entry.Scope["CorrelationId"]);
            Assert.Equal(Key, entry.Scope["ConversationKey"]);
            Assert.Equal(Connection, entry.Scope["ConnectionId"]);
            Assert.Equal(MessagingPlatform.WhatsApp, entry.Scope["Source"]);
        });
    }

    [Fact]
    public async Task ARedeliveredMessageIsLoggedAsAStoredDuplicate()
    {
        await Processor().ProcessAsync(Inbound(), CancellationToken.None);
        CapturingLogger logger = new();

        await Processor(logger: logger).ProcessAsync(Inbound(), CancellationToken.None);

        Assert.Contains(logger.Texts, t => t.StartsWith("message.stored Outcome=duplicate Sequence=1"));
        Assert.Contains(logger.Texts, t => t.StartsWith("message.persisted"));
    }

    // A failing stage is named with the message it failed for; before this only a metric moved.
    [Fact]
    public async Task AMongoFailureIsLoggedWithItsStageAndMessageAndNothingIsStoredOrAnnounced()
    {
        _store.Failure = new TimeoutException("MongoDB is down");
        CapturingLogger logger = new();

        await Assert.ThrowsAsync<TimeoutException>(() => Processor(logger: logger).ProcessAsync(Inbound(), CancellationToken.None));

        (LogLevel level, string text, IReadOnlyDictionary<string, object?> scope) = Assert.Single(logger.Entries, e => e.Text.StartsWith("message.process_failed"));
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("Stage=persist Reason=db_timeout Error=TimeoutException", text);
        Assert.Equal("whatsapp:wamid.A", scope["MessageId"]);
        Assert.DoesNotContain(logger.Texts, t => t.StartsWith("message.stored") || t.StartsWith("message.persisted"));
    }

    [Fact]
    public async Task AReceiptFailureIsLoggedAfterTheMessageIsStored()
    {
        _receipts.FailuresLeft = 1;
        CapturingLogger logger = new();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Processor(logger: logger).ProcessAsync(Inbound(), CancellationToken.None));

        Assert.Contains(logger.Texts, t => t.StartsWith("message.stored Outcome=stored"));
        Assert.Contains(logger.Texts, t => t.Contains("Stage=receipt Reason=receipt_failed"));
        Assert.DoesNotContain(logger.Texts, t => t.StartsWith("message.persisted"));
    }

    [Fact]
    public async Task APublishFailureIsLoggedAfterTheMessageIsStored()
    {
        _publisher.FailuresLeft = 1;
        CapturingLogger logger = new();

        await Assert.ThrowsAsync<IOException>(() => Processor(logger: logger).ProcessAsync(Inbound(), CancellationToken.None));

        Assert.Contains(logger.Texts, t => t.StartsWith("message.stored Outcome=stored"));
        Assert.Contains(logger.Texts, t => t.Contains("Stage=publish Reason=publish_failed"));
    }

    [Fact]
    public async Task AnUnprocessableMessageIsLoggedWithItsReasonAndMessage()
    {
        CapturingLogger logger = new();

        await Assert.ThrowsAsync<UnprocessableMessageException>(
            () => Processor(logger: logger).ProcessAsync(Inbound(body: "not json"), CancellationToken.None));

        (_, string text, IReadOnlyDictionary<string, object?> scope) = Assert.Single(logger.Entries, e => e.Text.StartsWith("message.unprocessable"));
        Assert.Contains("Reason=body_not_json", text);
        Assert.Equal("whatsapp:wamid.A", scope["MessageId"]);
        Assert.DoesNotContain(logger.Texts, t => t.StartsWith("message.stored"));
    }

    // The same rules against a real MongoDB: the unique index turns a second insert into "return the stored one".
    [MongoFact]
    public async Task RealMongo_DuplicateDeliveryStoresOnce()
    {
        MongoClient client = new(Environment.GetEnvironmentVariable("PARROT_TEST_MONGO"));
        string database = "parrot_test_" + Guid.NewGuid().ToString("N");
        IOptions<MongoDbSettings> settings = Options.Create(new MongoDbSettings { Database = database, Collection = "messages" });
        MessageSerializationRegistry.Register();
        try
        {
            _ = new MongoMessageRepository(client, settings,
                new MessageDocumentMapperFactory([new WhatsAppMessageDocumentMapper(), new FacebookMessageDocumentMapper(), new TikTokMessageDocumentMapper(), new InstagramMessageDocumentMapper()]));
            MongoStoredMessageStore store = new(client, settings);

            await Processor(store).ProcessAsync(Inbound(), CancellationToken.None);
            await Processor(store).ProcessAsync(Inbound(), CancellationToken.None);

            IMongoCollection<MessageDocument> messages = client.GetDatabase(database).GetCollection<MessageDocument>("messages");
            MessageDocument stored = Assert.Single(await messages.Find(FilterDefinition<MessageDocument>.Empty).ToListAsync());
            Assert.Equal(1, stored.Sequence);
            Assert.Equal(Key, stored.ConversationKey);
            Assert.Equal(2, _publisher.Published.Count);
        }
        finally
        {
            await client.DropDatabaseAsync(database);
        }
    }

    // --- A customer replying to (quoting) a message ---

    private static string QuotingBody(string quoted) => Body.Replace(
        "\"type\":\"text\",\"text\":{\"body\":\"Hello\"}",
        "\"type\":\"text\",\"text\":{\"body\":\"Hello\"},\"context\":{\"from\":\"2349011112222\",\"id\":\"" + quoted + "\"}");

    private static WhatsAppMessageDocument Earlier(string messageId, bool outgoing, string? providerId, string text) => new()
    {
        MessageId = messageId, OwnerUserId = "user-1", ConnectionId = Connection, Platform = MessagingPlatform.WhatsApp,
        ExternalAccountId = "waba-1", ChannelId = "phone-1", ChannelDisplayName = "", From = "2348012345678", Timestamp = "0",
        Type = "text", TextBody = text, ReceivedAt = DateTime.UtcNow, Outgoing = outgoing, ProviderMessageId = providerId,
    };

    [Fact]
    public async Task ACustomerQuotingTheirOwnEarlierMessage_IsStoredWithTheQuote()
    {
        _store.Documents.Add(Earlier("whatsapp:wamid.OLD", outgoing: false, null, "Do you deliver?"));

        await Processor().ProcessAsync(Inbound(body: QuotingBody("wamid.OLD")), CancellationToken.None);

        MessageReplyTo quote = _store.Documents.Single(d => d.MessageId == "whatsapp:wamid.A").ReplyTo!;
        Assert.Equal("whatsapp:wamid.OLD", quote.MessageId);
        Assert.False(quote.Outgoing);
        Assert.Equal("Do you deliver?", quote.Text);
        Assert.Equal("Do you deliver?", _publisher.Published.Single().Data.ReplyTo!.Text);
    }

    [Fact]
    public async Task ACustomerQuotingOurReply_IsMatchedByTheIdTheChannelGaveIt()
    {
        _store.Documents.Add(Earlier("outgoing:user-1:abc", outgoing: true, "wamid.OURS", "We open at 9"));

        await Processor().ProcessAsync(Inbound(body: QuotingBody("wamid.OURS")), CancellationToken.None);

        MessageReplyTo quote = _store.Documents.Single(d => d.MessageId == "whatsapp:wamid.A").ReplyTo!;
        Assert.Equal("outgoing:user-1:abc", quote.MessageId);
        Assert.True(quote.Outgoing);
    }

    [Fact]
    public async Task AQuoteOfAMessageTheInboxNeverHad_KeepsOnlyTheChannelsId()
    {
        await Processor().ProcessAsync(Inbound(body: QuotingBody("wamid.UNKNOWN")), CancellationToken.None);

        MessageReplyTo quote = _store.Documents.Single(d => d.MessageId == "whatsapp:wamid.A").ReplyTo!;
        Assert.Equal("wamid.UNKNOWN", quote.ProviderMessageId);
        Assert.Null(quote.MessageId);
    }

    [Fact]
    public async Task ProcessAsync_InboundWithLiveAgent_PublishesUserToAgent()
    {
        await Processor(agentChannelLookup: new FakeAgentChannelLookup(isLive: true))
            .ProcessAsync(Inbound(), CancellationToken.None);

        Assert.Equal(CommsType.UserToAgent, _publisher.Published.Single().Data.CommsType);
    }

    [Fact]
    public async Task ProcessAsync_InboundWithNoLiveAgent_PublishesUserToTenant()
    {
        await Processor(agentChannelLookup: new FakeAgentChannelLookup(isLive: false))
            .ProcessAsync(Inbound(), CancellationToken.None);

        Assert.Equal(CommsType.UserToTenant, _publisher.Published.Single().Data.CommsType);
    }

    [Fact]
    public async Task ProcessAsync_WithoutAgentLookup_InboundDefaultsToUserToTenant()
    {
        await Processor().ProcessAsync(Inbound(), CancellationToken.None);

        Assert.Equal(CommsType.UserToTenant, _publisher.Published.Single().Data.CommsType);
    }
}
