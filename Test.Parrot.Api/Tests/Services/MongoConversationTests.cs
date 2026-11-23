using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Parrot.Application.Conversations;
using Parrot.Application.DTOs.Integrations;
using Parrot.Domain.Entities.Messages;
using Parrot.Application.Integrations;
using Parrot.Domain.Enums;
using Parrot.Infrastructure.Conversations;
using Parrot.Infrastructure.Messaging;

namespace Test.Parrot.Api.Tests.Services;

public class MongoConversationTests
{
    public sealed class MongoFactAttribute : FactAttribute
    {
        public MongoFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("PARROT_TEST_MONGO") is null)
                Skip = "Set PARROT_TEST_MONGO to a disposable local MongoDB instance.";
        }
    }

    [MongoFact]
    public async Task RealMongoDeduplicatesIncomingAndOutgoingAndIsolatesHistory()
    {
        var client = new MongoClient(Environment.GetEnvironmentVariable("PARROT_TEST_MONGO"));
        string database = "parrot_test_" + Guid.NewGuid().ToString("N");
        var settings = Options.Create(new MongoDbSettings { Database = database, Collection = "messages" });
        try
        {
            var mappers = new MessageDocumentMapperFactory(
                [new WhatsAppMessageDocumentMapper(), new FacebookMessageDocumentMapper(), new TikTokMessageDocumentMapper(), new InstagramMessageDocumentMapper()]);
            var incoming = new MongoMessageRepository(client, settings, mappers);
            var store = new MongoConversationStore(client, settings);
            var connection = Guid.NewGuid();
            var message = new IncomingMessage
            {
                MessageId = "wamid.same", OwnerUserId = "owner", ConnectionId = connection,
                Platform = MessagingPlatform.WhatsApp, ExternalAccountId = "456", ChannelId = "123",
                ChannelDisplayName = "123", From = "2348012345678", Type = "text", TextBody = "Hello",
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ReceivedAt = DateTime.UtcNow,
            };
            await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => incoming.InsertAsync(message)));
            Assert.Single((await store.ReadAsync("owner", connection, message.From, null, default)).Messages);
            // The inbox needs to know which channel a conversation is on.
            Assert.Equal("whatsapp", Assert.Single(await store.ListAsync("owner", 0, 50, default)).Channel);
            // Old incoming documents have neither direction nor send-state fields.
            await client.GetDatabase(database).GetCollection<MessageDocument>("messages")
                .UpdateOneAsync(x => x.MessageId == message.MessageId,
                    Builders<MessageDocument>.Update.Unset(x => x.Outgoing).Unset(x => x.SendStatus));
            Assert.NotNull(await store.LatestIncomingAsync("owner", connection, message.From, default));
            Assert.Empty(await store.ListAsync("other", 0, 50, default));
            Assert.Empty((await store.ReadAsync("other", connection, message.From, null, default)).Messages);
            await Assert.ThrowsAsync<InvalidOperationException>(() => incoming.InsertAsync(message with { OwnerUserId = "other" }));
            var outgoing = new WhatsAppMessageDocument
            {
                MessageId = ConversationService.RequestKey("owner", Guid.NewGuid()), OwnerUserId = "owner", ConnectionId = connection,
                Platform = MessagingPlatform.WhatsApp, ExternalAccountId = "456", ChannelId = "123", ChannelDisplayName = "123",
                From = message.From, Type = "text", TextBody = "Reply", Timestamp = message.Timestamp,
                ReceivedAt = DateTime.UtcNow, Outgoing = true, SendStatus = "pending",
            };
            var reservations = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => store.ReserveAsync(outgoing, default)));
            Assert.Single(reservations.Where(x => x));
            await store.CompleteAsync(outgoing.MessageId, "accepted", "wamid.reply", default);
            var history = await store.ReadAsync("owner", connection, message.From, null, default);
            Assert.Equal(2, history.Messages.Count);
            Assert.Equal("accepted", history.Messages.Single(x => x.Outgoing).Status);
            for (int i = 0; i < 105; i++) await incoming.InsertAsync(message with { MessageId = $"wamid.{i}" });
            var firstPage = await store.ReadAsync("owner", connection, message.From, null, default);
            var secondPage = await store.ReadAsync("owner", connection, message.From, firstPage.NextBefore, default);
            Assert.Equal(100, firstPage.Messages.Count);
            Assert.Equal(7, secondPage.Messages.Count);
            Assert.Equal(107, firstPage.Messages.Concat(secondPage.Messages).Select(x => x.Id).Distinct().Count());
        }
        finally { await client.DropDatabaseAsync(database); }
    }

    // An agent's question names its chat by conversation key; the inbox names it by connection and customer.
    [MongoFact]
    public async Task FindByConversationKeysAsync_ReturnsTheOwnersInboxConversationForEachKeyAndNothingOfAnotherOwners()
    {
        MessageSerializationRegistry.Register();
        var client = new MongoClient(Environment.GetEnvironmentVariable("PARROT_TEST_MONGO"));
        string database = "parrot_test_" + Guid.NewGuid().ToString("N");
        var settings = Options.Create(new MongoDbSettings { Database = database, Collection = "messages" });
        try
        {
            var store = new MongoConversationStore(client, settings);
            var messages = client.GetDatabase(database).GetCollection<MessageDocument>("messages");
            const string ada = "whatsapp:2348012345678:2349011112222", bayo = "whatsapp:2348099999999:2349011112222";
            Guid oldConnection = Guid.NewGuid(), newConnection = Guid.NewGuid(), theirs = Guid.NewGuid();
            WhatsAppMessageDocument Message(string id, string owner, Guid connection, string from, string key, bool outgoing = false) => new()
            {
                MessageId = id, OwnerUserId = owner, ConnectionId = connection, Platform = MessagingPlatform.WhatsApp,
                ExternalAccountId = "2349011112222", ChannelId = "123", ChannelDisplayName = "123", From = from, Type = "text",
                TextBody = "Hello", Timestamp = "0", ReceivedAt = DateTime.UtcNow, Outgoing = outgoing, ConversationKey = key,
            };
            // Ada wrote before the channel was connected again, and after; the reply is stored under her too.
            await messages.InsertOneAsync(Message("wamid.1", "owner", oldConnection, "2348012345678", ada));
            await messages.InsertOneAsync(Message("wamid.2", "owner", newConnection, "2348012345678", ada));
            await messages.InsertOneAsync(Message("outgoing:3", "owner", newConnection, "2348012345678", ada, outgoing: true));
            // Another workspace's chat that happens to have the same key, and a key only they have.
            await messages.InsertOneAsync(Message("wamid.4", "other", theirs, "2348012345678", ada));
            await messages.InsertOneAsync(Message("wamid.5", "other", theirs, "2348099999999", bayo));

            IReadOnlyDictionary<string, (Guid Connection, string Sender)> found =
                await store.FindByConversationKeysAsync("owner", [ada, bayo, "whatsapp:unknown:2349011112222"], default);

            Assert.Equal((newConnection, "2348012345678"), Assert.Single(found).Value);
            Assert.Equal(ada, found.Keys.Single());
            Assert.Empty(await store.FindByConversationKeysAsync("owner", [], default));
            Assert.Equal((theirs, "2348099999999"), (await store.FindByConversationKeysAsync("other", [bayo], default))[bayo]);
        }
        finally { await client.DropDatabaseAsync(database); }
    }

    // A reply the team sent answers the agents' questions; the agent's own replies and sends that didn't go out don't.
    [MongoFact]
    public async Task LastTeamRepliesAsync_ReturnsTheNewestSentTeamReplyPerConversationOnly()
    {
        MessageSerializationRegistry.Register();
        var client = new MongoClient(Environment.GetEnvironmentVariable("PARROT_TEST_MONGO"));
        string database = "parrot_test_" + Guid.NewGuid().ToString("N");
        var settings = Options.Create(new MongoDbSettings { Database = database, Collection = "messages" });
        try
        {
            var store = new MongoConversationStore(client, settings);
            var messages = client.GetDatabase(database).GetCollection<MessageDocument>("messages");
            Guid connection = Guid.NewGuid();
            DateTime t0 = new(2026, 11, 23, 9, 0, 0, DateTimeKind.Utc);
            WhatsAppMessageDocument Message(string id, string owner, string from, int minute, bool outgoing, string status, bool? ai = null) => new()
            {
                MessageId = id, OwnerUserId = owner, ConnectionId = connection, Platform = MessagingPlatform.WhatsApp,
                ExternalAccountId = "2349011112222", ChannelId = "123", ChannelDisplayName = "123", From = from, Type = "text",
                TextBody = "Hello", Timestamp = "0", ReceivedAt = t0.AddMinutes(minute), Outgoing = outgoing, SendStatus = status, IsAiReply = ai,
            };
            const string ada = "2348012345678", bayo = "2348099999999";
            await messages.InsertManyAsync(
            [
                Message("ada.team", "owner", ada, 1, outgoing: true, "accepted"),
                Message("ada.delivered", "owner", ada, 2, outgoing: true, "delivered"),
                Message("ada.agent", "owner", ada, 3, outgoing: true, "accepted", ai: true),
                Message("ada.failed", "owner", ada, 4, outgoing: true, "failed"),
                Message("ada.pending", "owner", ada, 5, outgoing: true, "pending"),
                Message("ada.customer", "owner", ada, 6, outgoing: false, "received"),
                Message("ada.theirs", "other", ada, 7, outgoing: true, "accepted"),
                Message("bayo.agent", "owner", bayo, 8, outgoing: true, "accepted", ai: true),
            ]);

            IReadOnlyDictionary<(Guid Connection, string Sender), DateTime> replies =
                await store.LastTeamRepliesAsync("owner", [(connection, ada), (connection, bayo)], default);

            Assert.Equal(t0.AddMinutes(2), Assert.Single(replies).Value);
            Assert.Equal((connection, ada), replies.Keys.Single());
            Assert.Empty(await store.LastTeamRepliesAsync("owner", [], default));
        }
        finally { await client.DropDatabaseAsync(database); }
    }

    // The setup test finds a TikTok customer's message by the connection and its owner, whatever the platform's own
    // account id (here the business open id) is.
    [MongoFact]
    public async Task ATikTokTestMessageIsFoundByItsConnectionAndOwner()
    {
        var client = new MongoClient(Environment.GetEnvironmentVariable("PARROT_TEST_MONGO"));
        string database = "parrot_test_" + Guid.NewGuid().ToString("N");
        var settings = Options.Create(new MongoDbSettings { Database = database, Collection = "messages" });
        try
        {
            var mappers = new MessageDocumentMapperFactory(
                [new WhatsAppMessageDocumentMapper(), new FacebookMessageDocumentMapper(), new TikTokMessageDocumentMapper(), new InstagramMessageDocumentMapper()]);
            var repository = new MongoMessageRepository(client, settings, mappers);
            var connection = Guid.NewGuid();
            await repository.InsertAsync(new IncomingMessage
            {
                MessageId = "tiktok.test", OwnerUserId = "owner", ConnectionId = connection,
                Platform = MessagingPlatform.TikTok, ExternalAccountId = "business-open-id", ChannelId = "business-open-id",
                ChannelDisplayName = string.Empty, From = "customer", Type = "text", TextBody = "Hello",
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ReceivedAt = DateTime.UtcNow,
            });
            DateTime since = DateTime.UtcNow.AddMinutes(-1);

            Assert.NotNull(await repository.FindFirstAsync(connection, "owner", since));
            Assert.Null(await repository.FindFirstAsync(connection, "someone-else", since));
            Assert.Null(await repository.FindFirstAsync(Guid.NewGuid(), "owner", since));
            Assert.Null(await repository.FindFirstAsync(connection, "owner", DateTime.UtcNow.AddMinutes(1)));
        }
        finally { await client.DropDatabaseAsync(database); }
    }
}
