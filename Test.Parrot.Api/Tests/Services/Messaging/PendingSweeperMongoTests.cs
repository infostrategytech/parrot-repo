using Microsoft.Extensions.Options;
using MongoDB.Driver;
using MongoDB.Bson.IO;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;
using Parrot.Domain.Entities.Messages;
using Parrot.Domain.Enums;
using Parrot.Infrastructure.Messaging;

namespace Test.Parrot.Api.Tests.Services.Messaging;

// What the sweeper asks MongoDB, on a real server: that the queries find exactly the stale work, that the partial
// indexes can be created, and that the sweep uses them instead of scanning.
public class PendingSweeperMongoTests : IAsyncLifetime
{
    private static readonly DateTime Now = DateTime.UtcNow;
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

    private IOptions<MongoDbSettings> Settings => Options.Create(new MongoDbSettings { Database = _database, Collection = "messages" });

    private IMongoCollection<MessageDocument> Messages => _client!.GetDatabase(_database).GetCollection<MessageDocument>("messages");

    private MongoStoredMessageStore Store() => new(_client!, Settings);

    private static MessageDocument Incoming(string id, TimeSpan age, params string[] statuses) => new WhatsAppMessageDocument
    {
        MessageId = id, OwnerUserId = "user-1", ConnectionId = Guid.NewGuid(), Platform = MessagingPlatform.WhatsApp, ExternalAccountId = "waba",
        ChannelId = "phone", ChannelDisplayName = "", From = "2348012345678", Timestamp = "1", Type = "image", StoredAt = Now - age,
        Attachments = [.. statuses.Select(s => new MessageAttachment { Kind = "image", Status = s })],
    };

    private static MessageDocument Reply(string id, TimeSpan age, string status, DateTime? claimedAt = null) => new WhatsAppMessageDocument
    {
        MessageId = id, OwnerUserId = "user-1", ConnectionId = Guid.NewGuid(), Platform = MessagingPlatform.WhatsApp, ExternalAccountId = "waba",
        ChannelId = "phone", ChannelDisplayName = "", From = "2348012345678", Timestamp = "1", Type = "image", Outgoing = true, SendStatus = status,
        SendClaimedAt = claimedAt, StoredAt = Now - age, Attachments = [new MessageAttachment { Kind = "image", Status = MediaStatus.Ready }],
    };

    [Fact]
    public async Task Only_messages_with_a_pending_file_older_than_the_cut_off_are_found_oldest_first_with_the_pending_positions()
    {
        if (_client is null) return;
        await Messages.InsertManyAsync(
        [
            Incoming("old-pending", TimeSpan.FromMinutes(30), MediaStatus.Ready, MediaStatus.Pending, MediaStatus.Failed, MediaStatus.Pending),
            Incoming("older-pending", TimeSpan.FromHours(1), MediaStatus.Pending),
            Incoming("fresh-pending", TimeSpan.FromSeconds(30), MediaStatus.Pending),
            Incoming("settled", TimeSpan.FromHours(2), MediaStatus.Ready, MediaStatus.Failed),
        ]);

        IReadOnlyList<StaleMessage> found = await Store().FindStalePendingMediaAsync(Now - TimeSpan.FromMinutes(5), 10);

        Assert.Equal(["older-pending", "old-pending"], found.Select(m => m.MessageId));
        Assert.Equal([1, 3], found[1].PendingMedia);
        Assert.Equal("2348012345678", found[0].Recipient);
    }

    [Fact]
    public async Task The_batch_limit_is_respected()
    {
        if (_client is null) return;
        await Messages.InsertManyAsync(Enumerable.Range(0, 5).Select(i => Incoming($"m{i}", TimeSpan.FromHours(1 + i), MediaStatus.Pending)));

        Assert.Equal(2, (await Store().FindStalePendingMediaAsync(Now, 2)).Count);
    }

    [Fact]
    public async Task Stale_pending_replies_and_replies_sending_since_before_the_lease_are_found_and_nothing_else()
    {
        if (_client is null) return;
        await Messages.InsertManyAsync(
        [
            Reply("pending-old", TimeSpan.FromMinutes(10), "pending"),
            Reply("pending-fresh", TimeSpan.FromSeconds(10), "pending"),
            Reply("sending-long", TimeSpan.FromMinutes(30), "sending", Now - TimeSpan.FromMinutes(20)),
            Reply("sending-recent", TimeSpan.FromMinutes(30), "sending", Now - TimeSpan.FromSeconds(20)),
            Reply("accepted", TimeSpan.FromHours(3), "accepted"),
            Reply("failed", TimeSpan.FromHours(3), "failed"),
            Incoming("incoming", TimeSpan.FromHours(3), MediaStatus.Pending),
        ]);

        IReadOnlyList<StaleMessage> found = await Store().FindStalePendingSendsAsync(Now - TimeSpan.FromMinutes(2), Now - TimeSpan.FromMinutes(5), 10);

        Assert.Equal(["sending-long", "pending-old"], found.Select(m => m.MessageId));
        Assert.All(found, m => Assert.Empty(m.PendingMedia));
    }

    [Fact]
    public async Task A_reply_is_failed_only_while_it_is_still_pending()
    {
        if (_client is null) return;
        await Messages.InsertManyAsync([Reply("pending", TimeSpan.FromHours(1), "pending"), Reply("taken", TimeSpan.FromHours(1), "sending", Now)]);

        Assert.True(await Store().FailPendingSendAsync("pending"));
        Assert.False(await Store().FailPendingSendAsync("taken"));
        Assert.False(await Store().FailPendingSendAsync("missing"));

        Assert.Equal("failed", (await Store().FindAsync("pending"))!.SendStatus);
        Assert.Equal("sending", (await Store().FindAsync("taken"))!.SendStatus);
    }

    [Fact]
    public async Task The_partial_indexes_can_be_created_and_the_sweep_queries_use_them_instead_of_scanning()
    {
        if (_client is null) return;
        _ = new MongoMessageRepository(_client, Settings, new MessageDocumentMapperFactory([new WhatsAppMessageDocumentMapper(), new FacebookMessageDocumentMapper(), new TikTokMessageDocumentMapper(), new InstagramMessageDocumentMapper()]));
        await Messages.InsertManyAsync(Enumerable.Range(0, 300).Select(i => Incoming($"settled{i}", TimeSpan.FromHours(3), MediaStatus.Ready)));
        await Messages.InsertOneAsync(Incoming("pending", TimeSpan.FromHours(1), MediaStatus.Pending));
        await Messages.InsertOneAsync(Reply("reply", TimeSpan.FromHours(1), "pending"));

        List<string> names = (await (await Messages.Indexes.ListAsync()).ToListAsync()).Select(i => i["name"].AsString).ToList();
        Assert.Contains("PendingMedia_StoredAt", names);
        Assert.Contains("Send_pending_StoredAt", names);
        Assert.Contains("Send_sending_StoredAt", names);

        // Index scan, not a collection scan: what was examined is far fewer than the documents stored.
        var explain = await _client.GetDatabase(_database).RunCommandAsync<MongoDB.Bson.BsonDocument>(new MongoDB.Bson.BsonDocument
        {
            { "explain", new MongoDB.Bson.BsonDocument { { "find", "messages" }, { "filter", new MongoDB.Bson.BsonDocument { { "Attachments.Status", "pending" }, { "StoredAt", new MongoDB.Bson.BsonDocument("$lt", Now) } } } } },
            { "verbosity", "executionStats" },
        });
        Assert.True(explain["executionStats"]["totalDocsExamined"].ToInt64() < 50, MongoDB.Bson.BsonExtensionMethods.ToJson(explain));
    }
}
