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

// Pins and "mark as unread" against a real, temporary MongoDB: they are stored per user and conversation.
public class ConversationFlagTests
{
    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _database = "parrot_test_" + Guid.NewGuid().ToString("N");
        public MongoClient Client { get; } = new(Environment.GetEnvironmentVariable("PARROT_TEST_MONGO"));
        private IOptions<MongoDbSettings> Settings => Options.Create(new MongoDbSettings { Database = _database, Collection = "messages" });

        public Harness() => MessageSerializationRegistry.Register();

        public MongoConversationFlagStore Flags() => new(Client, Settings);
        public MongoConversationStore Conversations() => new(Client, Settings);

        public Task AddAsync(string owner, Guid connection, string customer, DateTime at, string text = "hello") =>
            Client.GetDatabase(_database).GetCollection<MessageDocument>("messages").InsertOneAsync(new WhatsAppMessageDocument
            {
                MessageId = "m" + Guid.NewGuid().ToString("N"), Platform = MessagingPlatform.WhatsApp, ExternalAccountId = "a",
                From = customer, Timestamp = new DateTimeOffset(at).ToUnixTimeSeconds().ToString(), Type = "text", ChannelId = "a",
                ChannelDisplayName = "a", Id = ObjectId.GenerateNewId(at).ToString(), OwnerUserId = owner, ConnectionId = connection,
                TextBody = text, ReceivedAt = at,
            });

        public async ValueTask DisposeAsync() => await Client.DropDatabaseAsync(_database);
    }

    [MongoFact]
    public async Task APinIsStoredPerUserAndLiftedByUnpinning()
    {
        await using Harness h = new();
        MongoConversationFlagStore flags = h.Flags();
        Guid connection = Guid.NewGuid();

        await flags.SetPinnedAsync("ade", connection, "2348012345678", true, default);

        ConversationFlags pin = (await flags.ListAsync("ade", default))[(connection, "2348012345678")];
        Assert.True(pin.Pinned);
        Assert.NotNull(pin.PinnedAt);
        Assert.False(pin.MarkedUnread);
        Assert.Empty(await flags.ListAsync("someone-else", default));

        await flags.SetPinnedAsync("ade", connection, "2348012345678", false, default);
        Assert.Empty(await flags.ListAsync("ade", default));
    }

    [MongoFact]
    public async Task APinCanBeSetAgainAfterItWasLifted()
    {
        await using Harness h = new();
        MongoConversationFlagStore flags = h.Flags();
        Guid connection = Guid.NewGuid();

        await flags.SetPinnedAsync("ade", connection, "c", true, default);
        await flags.SetPinnedAsync("ade", connection, "c", false, default);
        await flags.SetPinnedAsync("ade", connection, "c", true, default);

        Assert.True((await flags.ListAsync("ade", default))[(connection, "c")].Pinned);
    }

    [MongoFact]
    public async Task PinningAgainKeepsTheFirstPinTime()
    {
        await using Harness h = new();
        MongoConversationFlagStore flags = h.Flags();
        Guid connection = Guid.NewGuid();
        await flags.SetPinnedAsync("ade", connection, "c", true, default);
        DateTime first = (await flags.ListAsync("ade", default))[(connection, "c")].PinnedAt!.Value;

        await Task.Delay(20);
        await flags.SetPinnedAsync("ade", connection, "c", true, default);

        Assert.Equal(first, (await flags.ListAsync("ade", default))[(connection, "c")].PinnedAt!.Value);
    }

    [MongoFact]
    public async Task MarkedUnreadAndPinnedAreIndependent()
    {
        await using Harness h = new();
        MongoConversationFlagStore flags = h.Flags();
        Guid connection = Guid.NewGuid();
        await flags.SetPinnedAsync("ade", connection, "c", true, default);
        await flags.SetMarkedUnreadAsync("ade", connection, "c", true, default);

        ConversationFlags both = (await flags.ListAsync("ade", default))[(connection, "c")];
        Assert.True(both.Pinned);
        Assert.True(both.MarkedUnread);

        await flags.SetMarkedUnreadAsync("ade", connection, "c", false, default);
        ConversationFlags pinnedOnly = (await flags.ListAsync("ade", default))[(connection, "c")];
        Assert.True(pinnedOnly.Pinned);
        Assert.False(pinnedOnly.MarkedUnread);

        await flags.SetPinnedAsync("ade", connection, "c", false, default);
        Assert.Empty(await flags.ListAsync("ade", default));
    }

    [MongoFact]
    public async Task ConcurrentFirstPinsOfOneConversationEndAsOneRow()
    {
        await using Harness h = new();
        MongoConversationFlagStore flags = h.Flags();
        Guid connection = Guid.NewGuid();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => i % 2 == 0
            ? flags.SetPinnedAsync("ade", connection, "c", true, default)
            : flags.SetMarkedUnreadAsync("ade", connection, "c", true, default)));

        ConversationFlags flag = Assert.Single(await flags.ListAsync("ade", default)).Value;
        Assert.True(flag.Pinned);
        Assert.True(flag.MarkedUnread);
    }

    [MongoFact]
    public async Task FindReturnsOnlyTheAskedForConversationsOfThatOwner()
    {
        await using Harness h = new();
        Guid connection = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;
        await h.AddAsync("ade", connection, "old-customer", now.AddDays(-30), "ages ago");
        await h.AddAsync("ade", connection, "new-customer", now);
        await h.AddAsync("ade", connection, "other-customer", now.AddMinutes(-5));
        await h.AddAsync("someone-else", connection, "old-customer", now.AddDays(-1), "not yours");
        MongoConversationStore store = h.Conversations();

        IReadOnlyList<ConversationSummary> found = await store.FindAsync("ade", [(connection, "old-customer"), (connection, "no-such-customer")], default);

        ConversationSummary only = Assert.Single(found);
        Assert.Equal("old-customer", only.Sender);
        Assert.Equal("ages ago", only.Preview);
        Assert.Empty(await store.FindAsync("ade", [], default));
    }
}
