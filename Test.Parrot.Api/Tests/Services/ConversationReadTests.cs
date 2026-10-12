using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Parrot.Application.Conversations;
using Parrot.Domain.Entities.Messages;
using Parrot.Domain.Enums;
using Parrot.Domain.Exceptions;
using Parrot.Infrastructure.Conversations;
using Parrot.Infrastructure.Messaging;
using static Test.Parrot.Api.Tests.Services.MongoConversationTests;

namespace Test.Parrot.Api.Tests.Services;

// Unread state against a real, temporary MongoDB: the counting is a query, so a fake would test nothing.
public class ConversationReadTests
{
    private const string Owner = "owner";

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _database = "parrot_test_" + Guid.NewGuid().ToString("N");
        public MongoClient Client { get; } = new(Environment.GetEnvironmentVariable("PARROT_TEST_MONGO"));
        public IMongoCollection<MessageDocument> Messages { get; }

        public Harness()
        {
            MessageSerializationRegistry.Register();
            Messages = Client.GetDatabase(_database).GetCollection<MessageDocument>("messages");
        }

        // A day back by default, so the messages these tests store are all after the baseline.
        public MongoConversationReadStore Store(DateTime? baseline = null) => new(Client,
            Options.Create(new MongoDbSettings { Database = _database, Collection = "messages" }),
            Options.Create(new InboxSettings { UnreadBaseline = baseline ?? DateTime.UtcNow.AddDays(-1) }));

        public MongoConversationReadStore StoreWithSavedBaseline() => new(Client,
            Options.Create(new MongoDbSettings { Database = _database, Collection = "messages" }),
            Options.Create(new InboxSettings()));

        public Task AddAsync(params MessageDocument[] documents) => Messages.InsertManyAsync(documents);

        public async ValueTask DisposeAsync() => await Client.DropDatabaseAsync(_database);
    }

    private static int _counter;

    private static MessageDocument Message(MessagingPlatform platform, Guid connection, string customer,
        bool outgoing = false, string owner = Owner, DateTime? at = null)
    {
        string messageId = $"m{Interlocked.Increment(ref _counter)}";
        DateTime when = at ?? DateTime.UtcNow;
        string timestamp = new DateTimeOffset(when).ToUnixTimeSeconds().ToString();
        MessageDocument document = platform switch
        {
            MessagingPlatform.Facebook => new FacebookMessageDocument { MessageId = messageId, Platform = platform, ExternalAccountId = "a", From = customer, Timestamp = timestamp, Type = "text", ChannelId = "a", ChannelDisplayName = "a" },
            MessagingPlatform.TikTok => new TikTokMessageDocument { MessageId = messageId, Platform = platform, ExternalAccountId = "a", From = customer, Timestamp = timestamp, Type = "text", ChannelId = "a", ChannelDisplayName = "a" },
            _ => new WhatsAppMessageDocument { MessageId = messageId, Platform = platform, ExternalAccountId = "a", From = customer, Timestamp = timestamp, Type = "text", ChannelId = "a", ChannelDisplayName = "a" },
        };
        document.Id = ObjectId.GenerateNewId(when).ToString();
        document.OwnerUserId = owner;
        document.ConnectionId = connection;
        document.Outgoing = outgoing;
        document.TextBody = "hello";
        document.ReceivedAt = when;
        return document;
    }

    [MongoFact]
    public async Task EveryChannelCountsAndTheAgentsOwnRepliesNeverDo()
    {
        await using Harness h = new();
        Guid whatsApp = Guid.NewGuid(), facebook = Guid.NewGuid(), tikTok = Guid.NewGuid();
        await h.AddAsync(
            Message(MessagingPlatform.WhatsApp, whatsApp, "2348012345678"),
            Message(MessagingPlatform.WhatsApp, whatsApp, "2348012345678"),
            Message(MessagingPlatform.WhatsApp, whatsApp, "2348012345678", outgoing: true),
            Message(MessagingPlatform.Facebook, facebook, "psid-1"),
            Message(MessagingPlatform.TikTok, tikTok, "open-id-1"),
            Message(MessagingPlatform.TikTok, tikTok, "open-id-1"),
            Message(MessagingPlatform.TikTok, tikTok, "open-id-1"));
        MongoConversationReadStore store = h.Store();

        IReadOnlyDictionary<(Guid, string), int> counts = await store.CountUnreadAsync(Owner, Owner,
            [(whatsApp, "2348012345678"), (facebook, "psid-1"), (tikTok, "open-id-1")], default);

        Assert.Equal(2, counts[(whatsApp, "2348012345678")]);
        Assert.Equal(1, counts[(facebook, "psid-1")]);
        Assert.Equal(3, counts[(tikTok, "open-id-1")]);
        Assert.Equal(6, await store.TotalUnreadAsync(Owner, Owner, default));
    }

    [MongoFact]
    public async Task AReadConversationHasNoCountAndNothingForAnotherOwnerIsCounted()
    {
        await using Harness h = new();
        Guid connection = Guid.NewGuid();
        await h.AddAsync(Message(MessagingPlatform.Facebook, connection, "psid-1", owner: "someone-else"));
        MongoConversationReadStore store = h.Store();

        Assert.Empty(await store.CountUnreadAsync(Owner, Owner, [(connection, "psid-1")], default));
        Assert.Equal(0, await store.TotalUnreadAsync(Owner, Owner, default));
    }

    [MongoFact]
    public async Task OpeningAConversationMarksItReadAndALaterMessageIsUnreadAgain()
    {
        await using Harness h = new();
        Guid connection = Guid.NewGuid();
        MessageDocument first = Message(MessagingPlatform.WhatsApp, connection, "2348012345678");
        MessageDocument second = Message(MessagingPlatform.WhatsApp, connection, "2348012345678");
        MessageDocument third = Message(MessagingPlatform.WhatsApp, connection, "2348012345678");
        await h.AddAsync(first, second, third);
        MongoConversationReadStore store = h.Store();

        await store.MarkReadAsync(Owner, Owner, connection, "2348012345678", second.MessageId, default);
        Assert.Equal(1, await store.TotalUnreadAsync(Owner, Owner, default));

        // A late or repeated mark of an older message does not bring a message back.
        await store.MarkReadAsync(Owner, Owner, connection, "2348012345678", first.MessageId, default);
        Assert.Equal(1, await store.TotalUnreadAsync(Owner, Owner, default));

        await store.MarkReadAsync(Owner, Owner, connection, "2348012345678", third.MessageId, default);
        Assert.Equal(0, await store.TotalUnreadAsync(Owner, Owner, default));
        Assert.Empty(await store.CountUnreadAsync(Owner, Owner, [(connection, "2348012345678")], default));

        await h.AddAsync(Message(MessagingPlatform.WhatsApp, connection, "2348012345678"));
        Assert.Equal(1, await store.TotalUnreadAsync(Owner, Owner, default));
    }

    [MongoFact]
    public async Task MarkingReadOnlyClearsWhatWasSeen()
    {
        await using Harness h = new();
        Guid connection = Guid.NewGuid();
        MessageDocument seen = Message(MessagingPlatform.Facebook, connection, "psid-1");
        MessageDocument arrivedWhileReading = Message(MessagingPlatform.Facebook, connection, "psid-1");
        await h.AddAsync(seen, arrivedWhileReading);
        MongoConversationReadStore store = h.Store();

        await store.MarkReadAsync(Owner, Owner, connection, "psid-1", seen.MessageId, default);

        Assert.Equal(1, (await store.CountUnreadAsync(Owner, Owner, [(connection, "psid-1")], default))[(connection, "psid-1")]);
    }

    [MongoFact]
    public async Task EachUserKeepsTheirOwnPosition()
    {
        await using Harness h = new();
        Guid connection = Guid.NewGuid();
        MessageDocument message = Message(MessagingPlatform.TikTok, connection, "open-id-1");
        await h.AddAsync(message);
        MongoConversationReadStore store = h.Store();

        await store.MarkReadAsync(Owner, "agent-a", connection, "open-id-1", message.MessageId, default);

        Assert.Equal(0, await store.TotalUnreadAsync(Owner, "agent-a", default));
        Assert.Equal(1, await store.TotalUnreadAsync(Owner, "agent-b", default));
    }

    [MongoFact]
    public async Task ReadStateSurvivesARestart()
    {
        await using Harness h = new();
        Guid connection = Guid.NewGuid();
        MessageDocument read = Message(MessagingPlatform.WhatsApp, connection, "2348012345678");
        await h.AddAsync(read, Message(MessagingPlatform.WhatsApp, connection, "2348012345678"));
        await h.Store().MarkReadAsync(Owner, Owner, connection, "2348012345678", read.MessageId, default);

        // A new store is a new process: nothing is held in memory.
        Assert.Equal(1, await h.Store().TotalUnreadAsync(Owner, Owner, default));
    }

    [MongoFact]
    public async Task MessagesBeforeTheBaselineAreReadAndLaterOnesAreNot()
    {
        await using Harness h = new();
        Guid connection = Guid.NewGuid();
        DateTime baseline = DateTime.UtcNow.AddHours(-1);
        await h.AddAsync(
            Message(MessagingPlatform.WhatsApp, connection, "2348012345678", at: baseline.AddDays(-3)),
            Message(MessagingPlatform.WhatsApp, connection, "2348012345678", at: baseline.AddMinutes(-5)),
            Message(MessagingPlatform.WhatsApp, connection, "2348012345678", at: baseline.AddMinutes(5)));
        MongoConversationReadStore store = h.Store(baseline);

        Assert.Equal(1, await store.TotalUnreadAsync(Owner, Owner, default));
        Assert.Equal(1, (await store.CountUnreadAsync(Owner, Owner, [(connection, "2348012345678")], default))[(connection, "2348012345678")]);
    }

    [MongoFact]
    public async Task TheSavedBaselineIsFixedAtFirstStartAndDoesNotMove()
    {
        await using Harness h = new();
        Guid connection = Guid.NewGuid();
        await h.AddAsync(Message(MessagingPlatform.WhatsApp, connection, "2348012345678", at: DateTime.UtcNow.AddMinutes(-10)));
        MongoConversationReadStore first = h.StoreWithSavedBaseline();
        // The baseline is the start's own second, so the next message is stored after it.
        await Task.Delay(1100);
        await h.AddAsync(Message(MessagingPlatform.WhatsApp, connection, "2348012345678"));

        Assert.Equal(1, await first.TotalUnreadAsync(Owner, Owner, default));

        // A restart reads the saved time back. If it took "now" instead, the message above would be read.
        await Task.Delay(1100);
        Assert.Equal(1, await h.StoreWithSavedBaseline().TotalUnreadAsync(Owner, Owner, default));
    }

    [MongoFact]
    public async Task OnlyAMessageOfThatConversationCanBeMarked()
    {
        await using Harness h = new();
        Guid connection = Guid.NewGuid();
        MessageDocument mine = Message(MessagingPlatform.WhatsApp, connection, "2348012345678");
        MessageDocument otherCustomer = Message(MessagingPlatform.WhatsApp, connection, "2349011112222");
        MessageDocument otherOwner = Message(MessagingPlatform.WhatsApp, connection, "2348012345678", owner: "someone-else");
        await h.AddAsync(mine, otherCustomer, otherOwner);
        MongoConversationReadStore store = h.Store();

        await Assert.ThrowsAsync<NotFoundException>(() => store.MarkReadAsync(Owner, Owner, connection, "2348012345678", otherCustomer.MessageId, default));
        await Assert.ThrowsAsync<NotFoundException>(() => store.MarkReadAsync(Owner, Owner, connection, "2348012345678", otherOwner.MessageId, default));
        // An id that matches no message cannot read anything, now or for later arrivals.
        await Assert.ThrowsAsync<NotFoundException>(() => store.MarkReadAsync(Owner, Owner, connection, "2348012345678", "no-such-message", default));
        // The failed marks changed nothing: both of this owner's customers are still unread.
        Assert.Equal(2, await store.TotalUnreadAsync(Owner, Owner, default));
    }

    [MongoFact]
    public async Task ManyConversationsAreCountedInBatches()
    {
        await using Harness h = new();
        Guid connection = Guid.NewGuid();
        await h.AddAsync(Enumerable.Range(0, 450).Select(i => Message(MessagingPlatform.Facebook, connection, $"psid-{i}")).ToArray());
        MongoConversationReadStore store = h.Store();

        Assert.Equal(450, await store.TotalUnreadAsync(Owner, Owner, default));
        Assert.Equal(450, (await store.CountUnreadAsync(Owner, Owner,
            Enumerable.Range(0, 450).Select(i => (connection, $"psid-{i}")).ToList(), default)).Count);
    }
}
