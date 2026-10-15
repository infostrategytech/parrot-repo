using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Parrot.Application.Messaging;
using Parrot.Domain.Entities.Messages;
using Parrot.Domain.Enums;
using Parrot.Infrastructure.Conversations;
using Parrot.Infrastructure.Messaging;

namespace Test.Parrot.Api.Tests.Services.Messaging;

// The compare-and-sets that make a queued reply reach the channel at most once, on real MongoDB: an in-memory
// fake cannot show that two workers racing for one reply really get one winner.
public class OutboundMongoTests : IAsyncLifetime
{
    private readonly string _database = "parrot_test_" + Guid.NewGuid().ToString("N");
    private MongoClient? _client;

    public Task InitializeAsync()
    {
        string? connection = Environment.GetEnvironmentVariable("PARROT_TEST_MONGO");
        if (connection is not null) _client = new MongoClient(connection);
        MessageSerializationRegistry.Register();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_client is not null) await _client.DropDatabaseAsync(_database);
    }

    private IMongoCollection<MessageDocument> Messages => _client!.GetDatabase(_database).GetCollection<MessageDocument>("messages");

    private IOptions<MongoDbSettings> Settings => Options.Create(new MongoDbSettings { Database = _database, Collection = "messages" });

    private static MessageDocument Reply(string id, string status = "pending", bool outgoing = true) => new WhatsAppMessageDocument
    {
        MessageId = id, OwnerUserId = "user-1", Platform = MessagingPlatform.WhatsApp, ExternalAccountId = "waba", ChannelId = "phone",
        ChannelDisplayName = "", From = "2348012345678", Timestamp = "1", Type = "image", Outgoing = outgoing, SendStatus = status,
    };

    [Fact]
    public async Task Of_many_workers_racing_for_one_reply_exactly_one_claims_it()
    {
        if (_client is null) return;
        await Messages.InsertOneAsync(Reply("r1"));
        MongoStoredMessageStore store = new(_client, Settings);

        bool[] results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => store.ClaimSendAsync("r1", DateTime.UtcNow)));

        Assert.Single(results, won => won);
        MessageDocument stored = (await store.FindAsync("r1"))!;
        Assert.Equal("sending", stored.SendStatus);
        Assert.NotNull(stored.SendClaimedAt);
    }

    [Fact]
    public async Task Only_a_pending_outgoing_reply_can_be_claimed()
    {
        if (_client is null) return;
        await Messages.InsertManyAsync([Reply("done", "accepted"), Reply("incoming", "received", outgoing: false), Reply("sending", "sending")]);
        MongoStoredMessageStore store = new(_client, Settings);

        Assert.False(await store.ClaimSendAsync("done", DateTime.UtcNow));
        Assert.False(await store.ClaimSendAsync("incoming", DateTime.UtcNow));
        Assert.False(await store.ClaimSendAsync("sending", DateTime.UtcNow));
        Assert.False(await store.ClaimSendAsync("missing", DateTime.UtcNow));
    }

    [Fact]
    public async Task A_claimed_send_is_completed_once_and_a_second_completion_changes_nothing()
    {
        if (_client is null) return;
        await Messages.InsertOneAsync(Reply("r1"));
        MongoStoredMessageStore store = new(_client, Settings);
        await store.ClaimSendAsync("r1", DateTime.UtcNow);

        Assert.True(await store.CompleteSendAsync("r1", "accepted", "wamid.1"));
        Assert.False(await store.CompleteSendAsync("r1", "failed", null));

        MessageDocument stored = (await store.FindAsync("r1"))!;
        Assert.Equal("accepted", stored.SendStatus);
        Assert.Equal("wamid.1", stored.ProviderMessageId);
    }

    [Fact]
    public async Task A_reply_that_was_never_claimed_cannot_be_completed()
    {
        if (_client is null) return;
        await Messages.InsertOneAsync(Reply("r1"));

        Assert.False(await new MongoStoredMessageStore(_client, Settings).CompleteSendAsync("r1", "accepted", "wamid.1"));
    }

    [Fact]
    public async Task The_API_fails_a_reply_only_while_it_is_still_pending()
    {
        if (_client is null) return;
        await Messages.InsertManyAsync([Reply("pending"), Reply("taken", "sending")]);
        MongoConversationStore conversations = new(_client, Settings);

        Assert.True(await conversations.FailPendingAsync("pending", default));
        Assert.False(await conversations.FailPendingAsync("taken", default));

        Assert.Equal("failed", (await new MongoStoredMessageStore(_client, Settings).FindAsync("pending"))!.SendStatus);
        Assert.Equal("sending", (await new MongoStoredMessageStore(_client, Settings).FindAsync("taken"))!.SendStatus);
    }
}
