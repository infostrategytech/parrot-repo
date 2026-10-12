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
                [new WhatsAppMessageDocumentMapper(), new FacebookMessageDocumentMapper(), new InstagramMessageDocumentMapper()]);
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
            Assert.Single(await store.ListAsync("owner", 0, 50, default));
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
}
