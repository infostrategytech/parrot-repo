using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Parrot.Application.Conversations;
using Parrot.Domain.Entities.Messages;
using Parrot.Domain.Enums;
using Parrot.Infrastructure.Conversations;
using Parrot.Infrastructure.Messaging;
using static Test.Parrot.Api.Tests.Services.MongoConversationTests;

namespace Test.Parrot.Api.Tests.Services;

// What a channel reports back about a reply, against a real temporary MongoDB: the updates are
// database expressions, so a fake would test nothing.
public class DeliveryReceiptsTests
{
    private const string Owner = "owner";
    private const string Customer = "2348012345678";
    private static readonly Guid Connection = Guid.NewGuid();

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _database = "parrot_test_" + Guid.NewGuid().ToString("N");
        public MongoClient Client { get; } = new(Environment.GetEnvironmentVariable("PARROT_TEST_MONGO"));
        public IMongoCollection<MessageDocument> Messages { get; }
        public IMongoCollection<BsonDocument> Parked { get; }
        public MongoConversationStore Store { get; }
        public MongoDeliveryReceipts Receipts { get; }

        public Harness()
        {
            MessageSerializationRegistry.Register();
            IOptions<MongoDbSettings> settings = Options.Create(new MongoDbSettings { Database = _database, Collection = "messages" });
            Store = new MongoConversationStore(Client, settings);
            Receipts = new MongoDeliveryReceipts(Client, settings, NullLogger<MongoDeliveryReceipts>.Instance);
            Messages = Client.GetDatabase(_database).GetCollection<MessageDocument>("messages");
            Parked = Client.GetDatabase(_database).GetCollection<BsonDocument>("delivery_receipts_parked");
        }

        // A reply Meta has accepted, stored the way a send stores it.
        public async Task<string> ReplyAsync(string? providerId = "wamid.R1", Guid? connection = null, string status = "accepted",
            string customer = Customer, DateTime? sentAt = null)
        {
            WhatsAppMessageDocument doc = new()
            {
                MessageId = "outgoing:" + Guid.NewGuid(), Platform = MessagingPlatform.WhatsApp, ExternalAccountId = "a", From = customer,
                Timestamp = "0", Type = "text", ChannelId = "a", ChannelDisplayName = "a", TextBody = "Hello",
                OwnerUserId = Owner, ConnectionId = connection ?? Connection, Outgoing = true, SendStatus = status,
                ProviderMessageId = providerId, ReceivedAt = DateTime.UtcNow, SentAt = sentAt ?? DateTime.UtcNow,
            };
            await Messages.InsertOneAsync(doc);
            return doc.MessageId;
        }

        public Task<MessageDocument> GetAsync(string messageId) => Messages.Find(x => x.MessageId == messageId).FirstAsync();

        // The status the inbox is given, through the real read path.
        public async Task<string> ShownAsync(string messageId, Guid? connection = null) =>
            (await Store.ReadAsync(Owner, connection ?? Connection, Customer, null, default)).Messages.Single(m => m.Id == messageId).Status;

        public async ValueTask DisposeAsync() => await Client.DropDatabaseAsync(_database);
    }

    private static DateTime At(int seconds) => new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);

    [MongoFact]
    public async Task AReplyMovesFromAcceptedToDeliveredToRead()
    {
        await using Harness h = new();
        string id = await h.ReplyAsync();
        Assert.Equal("accepted", await h.ShownAsync(id));

        await h.Receipts.RecordAsync(Connection, "wamid.R1", DeliveryState.Delivered, At(5), default);
        Assert.Equal("delivered", await h.ShownAsync(id));

        await h.Receipts.RecordAsync(Connection, "wamid.R1", DeliveryState.Read, At(60), default);
        Assert.Equal("read", await h.ShownAsync(id));

        MessageDocument stored = await h.GetAsync(id);
        Assert.Equal(At(5), stored.DeliveredAt);
        Assert.Equal(At(60), stored.ReadAt);
    }

    [MongoFact]
    public async Task ReadWithoutADeliveredFirstStillMeansDelivered()
    {
        await using Harness h = new();
        string id = await h.ReplyAsync();

        await h.Receipts.RecordAsync(Connection, "wamid.R1", DeliveryState.Read, At(30), default);

        MessageDocument stored = await h.GetAsync(id);
        Assert.Equal(At(30), stored.ReadAt);
        Assert.Equal(At(30), stored.DeliveredAt);
    }

    [MongoFact]
    public async Task OutOfOrderAndRepeatedReceiptsKeepTheEarliestTimes()
    {
        await using Harness h = new();
        string id = await h.ReplyAsync();

        await h.Receipts.RecordAsync(Connection, "wamid.R1", DeliveryState.Read, At(60), default);
        await h.Receipts.RecordAsync(Connection, "wamid.R1", DeliveryState.Delivered, At(5), default);
        await h.Receipts.RecordAsync(Connection, "wamid.R1", DeliveryState.Delivered, At(90), default);
        await h.Receipts.RecordAsync(Connection, "wamid.R1", DeliveryState.Read, At(120), default);

        MessageDocument stored = await h.GetAsync(id);
        Assert.Equal(At(5), stored.DeliveredAt);
        Assert.Equal(At(60), stored.ReadAt);
        Assert.Equal("read", await h.ShownAsync(id));
    }

    [MongoFact]
    public async Task AFailureAfterAcceptanceShowsFailedButNeverOverturnsADeliveredReply()
    {
        await using Harness h = new();
        string failing = await h.ReplyAsync("wamid.F1");
        string delivered = await h.ReplyAsync("wamid.F2");
        await h.Receipts.RecordAsync(Connection, "wamid.F2", DeliveryState.Delivered, At(5), default);

        await h.Receipts.RecordAsync(Connection, "wamid.F1", DeliveryState.Failed, At(10), default);
        await h.Receipts.RecordAsync(Connection, "wamid.F2", DeliveryState.Failed, At(10), default);

        Assert.Equal("failed", await h.ShownAsync(failing));
        Assert.Equal("delivered", await h.ShownAsync(delivered));
    }

    [MongoFact]
    public async Task AnotherConnectionsReceiptCannotTouchThisReply()
    {
        await using Harness h = new();
        string id = await h.ReplyAsync();

        await h.Receipts.RecordAsync(Guid.NewGuid(), "wamid.R1", DeliveryState.Read, At(5), default);

        Assert.Equal("accepted", await h.ShownAsync(id));
        Assert.Null((await h.GetAsync(id)).ReadAt);
    }

    [MongoFact]
    public async Task OnlyOurRepliesTakeReceipts()
    {
        await using Harness h = new();
        WhatsAppMessageDocument incoming = new()
        {
            MessageId = "whatsapp:in", Platform = MessagingPlatform.WhatsApp, ExternalAccountId = "a", From = Customer, Timestamp = "0",
            Type = "text", ChannelId = "a", ChannelDisplayName = "a", OwnerUserId = Owner, ConnectionId = Connection,
            ProviderMessageId = "wamid.R1", ReceivedAt = DateTime.UtcNow,
        };
        await h.Messages.InsertOneAsync(incoming);

        await h.Receipts.RecordAsync(Connection, "wamid.R1", DeliveryState.Read, At(5), default);

        Assert.Null((await h.GetAsync("whatsapp:in")).ReadAt);
    }

    [MongoFact]
    public async Task AReceiptThatBeatsTheStoredChannelIdIsKeptAndAppliedOnceTheSendHasIt()
    {
        await using Harness h = new();
        // The send has reserved the reply but has not yet stored Meta's id.
        string id = await h.ReplyAsync(providerId: null, status: "pending");

        await h.Receipts.RecordAsync(Connection, "wamid.LATE", DeliveryState.Delivered, At(2), default);
        await h.Receipts.RecordAsync(Connection, "wamid.LATE", DeliveryState.Read, At(8), default);
        Assert.Equal(1, await h.Parked.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        Assert.Equal("pending", await h.ShownAsync(id));

        await h.Store.CompleteAsync(id, "accepted", "wamid.LATE", default);
        await h.Receipts.ApplyParkedAsync(Connection, "wamid.LATE", default);

        Assert.Equal("read", await h.ShownAsync(id));
        MessageDocument stored = await h.GetAsync(id);
        Assert.Equal(At(2), stored.DeliveredAt);
        Assert.Equal(At(8), stored.ReadAt);
        Assert.Equal(0, await h.Parked.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [MongoFact]
    public async Task AParkedFailureIsAppliedToo()
    {
        await using Harness h = new();
        string id = await h.ReplyAsync(providerId: null, status: "pending");
        await h.Receipts.RecordAsync(Connection, "wamid.LATE", DeliveryState.Failed, At(2), default);

        await h.Store.CompleteAsync(id, "accepted", "wamid.LATE", default);
        await h.Receipts.ApplyParkedAsync(Connection, "wamid.LATE", default);

        Assert.Equal("failed", await h.ShownAsync(id));
    }

    [MongoFact]
    public async Task AParkedReceiptBelongsToItsConnectionOnly()
    {
        await using Harness h = new();
        Guid other = Guid.NewGuid();
        string id = await h.ReplyAsync(providerId: null, status: "pending");
        await h.Receipts.RecordAsync(other, "wamid.LATE", DeliveryState.Read, At(2), default);

        await h.Store.CompleteAsync(id, "accepted", "wamid.LATE", default);
        await h.Receipts.ApplyParkedAsync(Connection, "wamid.LATE", default);

        Assert.Equal("accepted", await h.ShownAsync(id));
        Assert.Equal(1, await h.Parked.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [MongoFact]
    public async Task ParkedReceiptsExpireOnTheirOwn()
    {
        await using Harness h = new();
        await h.Receipts.RecordAsync(Connection, "wamid.NEVER", DeliveryState.Read, At(2), default);

        List<BsonDocument> indexes = await (await h.Parked.Indexes.ListAsync()).ToListAsync();
        Assert.Contains(indexes, i => i.Contains("expireAfterSeconds") && i["expireAfterSeconds"].ToInt32() == 24 * 3600);
    }

    [MongoFact]
    public async Task ApplyingNothingParkedIsHarmless()
    {
        await using Harness h = new();
        string id = await h.ReplyAsync();

        await h.Receipts.ApplyParkedAsync(Connection, "wamid.R1", default);

        Assert.Equal("accepted", await h.ShownAsync(id));
    }

    // Messenger's read receipt names no message: everything sent to that customer before the watermark was read.
    [MongoFact]
    public async Task AWatermarkReadsEveryEarlierReplyToThatCustomerAndNothingElse()
    {
        await using Harness h = new();
        string early = await h.ReplyAsync("m_1", sentAt: At(-20));
        string justBefore = await h.ReplyAsync("m_2", sentAt: At(-1));
        string after = await h.ReplyAsync("m_3", sentAt: At(30));
        string otherCustomer = await h.ReplyAsync("m_4", customer: "other-psid", sentAt: At(-20));
        string otherConnection = await h.ReplyAsync("m_5", connection: Guid.NewGuid(), sentAt: At(-20));
        string neverAccepted = await h.ReplyAsync(providerId: null, status: "pending", sentAt: At(-20));

        await h.Receipts.RecordUpToAsync(Connection, Customer, DeliveryState.Read, At(10), upTo: At(0), default);

        foreach (string read in new[] { early, justBefore })
        {
            MessageDocument stored = await h.GetAsync(read);
            Assert.Equal(At(10), stored.ReadAt);
            Assert.Equal(At(10), stored.DeliveredAt);
        }
        foreach (string untouched in new[] { after, otherCustomer, otherConnection, neverAccepted })
        {
            MessageDocument stored = await h.GetAsync(untouched);
            Assert.Null(stored.ReadAt);
            Assert.Null(stored.DeliveredAt);
        }
    }

    [MongoFact]
    public async Task ADeliveryWatermarkAndALaterReadKeepTheEarliestTimes()
    {
        await using Harness h = new();
        string id = await h.ReplyAsync("m_1", sentAt: At(-5));

        await h.Receipts.RecordUpToAsync(Connection, Customer, DeliveryState.Read, At(60), upTo: At(0), default);
        await h.Receipts.RecordUpToAsync(Connection, Customer, DeliveryState.Delivered, At(3), upTo: At(0), default);
        await h.Receipts.RecordUpToAsync(Connection, Customer, DeliveryState.Read, At(90), upTo: At(0), default);

        MessageDocument stored = await h.GetAsync(id);
        Assert.Equal(At(3), stored.DeliveredAt);
        Assert.Equal(At(60), stored.ReadAt);
    }

    [MongoFact]
    public async Task AWatermarkCannotReportAFailure()
    {
        await using Harness h = new();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            h.Receipts.RecordUpToAsync(Connection, Customer, DeliveryState.Failed, At(0), At(0), default));
    }
}
