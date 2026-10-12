using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
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

        public Task<MessageDocument> InsertOrGetAsync(MessageDocument document, CancellationToken cancellationToken = default)
        {
            if (Failure is not null) throw Failure;
            MessageDocument? existing = Documents.FirstOrDefault(d => d.MessageId == document.MessageId);
            if (existing is not null) return Task.FromResult(existing);
            Documents.Add(document);
            return Task.FromResult(document);
        }
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

    private readonly FakeStore _store = new();
    private readonly FakeSequence _sequence = new();
    private readonly FakePublisher _publisher = new();
    private readonly FakeReceipts _receipts = new();

    private InboundMessageProcessor Processor(IStoredMessageStore? store = null) => new(
        new InboundMessageDecoders([new WhatsAppInboundDecoder(), new FacebookInboundDecoder()]),
        store ?? _store, _sequence,
        new MessageDocumentMapperFactory([new WhatsAppMessageDocumentMapper(), new FacebookMessageDocumentMapper(), new InstagramMessageDocumentMapper()]),
        _receipts, _publisher, NullLogger<InboundMessageProcessor>.Instance);

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
                new MessageDocumentMapperFactory([new WhatsAppMessageDocumentMapper(), new FacebookMessageDocumentMapper(), new InstagramMessageDocumentMapper()]));
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
}
