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

public class ReplyWindowTests
{
    private static readonly DateTime Now = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(MessagingPlatform.WhatsApp, 24)]
    [InlineData(MessagingPlatform.Facebook, 24)]
    [InlineData(MessagingPlatform.TikTok, 48)]
    public void EachChannelHasItsOwnWindow(MessagingPlatform platform, int hours) =>
        Assert.Equal(TimeSpan.FromHours(hours), ReplyWindow.For(platform));

    [Theory]
    [InlineData(MessagingPlatform.WhatsApp, 23, true)]
    [InlineData(MessagingPlatform.WhatsApp, 25, false)]
    [InlineData(MessagingPlatform.Facebook, 25, false)]
    [InlineData(MessagingPlatform.TikTok, 25, true)]
    [InlineData(MessagingPlatform.TikTok, 47, true)]
    [InlineData(MessagingPlatform.TikTok, 49, false)]
    public void TheWindowIsOpenUntilItsEnd(MessagingPlatform platform, int hoursSince, bool open) =>
        Assert.Equal(open, ReplyWindow.IsOpen(platform, Now.AddHours(-hoursSince), Now));

    [Fact]
    public void ARepliesExactlyAtTheEndIsStillAllowed() =>
        Assert.True(ReplyWindow.IsOpen(MessagingPlatform.WhatsApp, Now.AddHours(-24), Now));

    private static MessageDocument Message(MessagingPlatform platform, Guid connection, string customer, bool outgoing,
        DateTime receivedAt, DateTime? sentAt)
    {
        string timestamp = new DateTimeOffset(receivedAt).ToUnixTimeSeconds().ToString();
        MessageDocument document = platform == MessagingPlatform.TikTok
            ? new TikTokMessageDocument { MessageId = Guid.NewGuid().ToString(), Platform = platform, ExternalAccountId = "a", From = customer, Timestamp = timestamp, Type = "text", ChannelId = "a", ChannelDisplayName = "a" }
            : new WhatsAppMessageDocument { MessageId = Guid.NewGuid().ToString(), Platform = platform, ExternalAccountId = "a", From = customer, Timestamp = timestamp, Type = "text", ChannelId = "a", ChannelDisplayName = "a" };
        document.OwnerUserId = "owner";
        document.ConnectionId = connection;
        document.Outgoing = outgoing;
        document.TextBody = "hello";
        document.ReceivedAt = receivedAt;
        document.SentAt = sentAt;
        return document;
    }

    // Each document is inserted in order, so a later one is also later by _id, as the list sorts.
    private static async Task<IReadOnlyList<ConversationSummary>> ListAsync(params MessageDocument[] documents)
    {
        MessageSerializationRegistry.Register();
        MongoClient client = new(Environment.GetEnvironmentVariable("PARROT_TEST_MONGO"));
        string database = "parrot_test_" + Guid.NewGuid().ToString("N");
        try
        {
            IOptions<MongoDbSettings> settings = Options.Create(new MongoDbSettings { Database = database, Collection = "messages" });
            MongoConversationStore store = new(client, settings);
            IMongoCollection<MessageDocument> collection = client.GetDatabase(database).GetCollection<MessageDocument>("messages");
            foreach (MessageDocument document in documents) await collection.InsertOneAsync(document);
            return await store.ListAsync("owner", 0, 50, default);
        }
        finally { await client.DropDatabaseAsync(database); }
    }

    [MongoFact]
    public async Task TheListGivesWhenTheCustomerLastWroteNotWhenTheAgentReplied()
    {
        Guid connection = Guid.NewGuid();
        DateTime wrote = DateTime.UtcNow.AddHours(-2);
        ConversationSummary row = Assert.Single(await ListAsync(
            Message(MessagingPlatform.WhatsApp, connection, "2348012345678", false, wrote, wrote),
            Message(MessagingPlatform.WhatsApp, connection, "2348012345678", true, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(-1))));

        Assert.Equal(wrote, row.LastCustomerMessageAt!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal(24, row.ReplyWindowHours);
    }

    [MongoFact]
    public async Task TheNewestIncomingMessageStartsTheWindow()
    {
        Guid connection = Guid.NewGuid();
        DateTime newest = DateTime.UtcNow.AddMinutes(-10);
        ConversationSummary row = Assert.Single(await ListAsync(
            Message(MessagingPlatform.WhatsApp, connection, "2348012345678", false, DateTime.UtcNow.AddHours(-5), DateTime.UtcNow.AddHours(-5)),
            Message(MessagingPlatform.WhatsApp, connection, "2348012345678", false, newest, newest)));

        Assert.Equal(newest, row.LastCustomerMessageAt!.Value, TimeSpan.FromSeconds(1));
    }

    [MongoFact]
    public async Task AMessageStoredBeforeSentAtExistedUsesItsReceivedTime()
    {
        Guid connection = Guid.NewGuid();
        DateTime received = DateTime.UtcNow.AddHours(-3);
        ConversationSummary row = Assert.Single(await ListAsync(
            Message(MessagingPlatform.WhatsApp, connection, "2348012345678", false, received, sentAt: null)));

        Assert.Equal(received, row.LastCustomerMessageAt!.Value, TimeSpan.FromSeconds(1));
    }

    [MongoFact]
    public async Task TikTokGetsTheLongerWindow()
    {
        ConversationSummary row = Assert.Single(await ListAsync(
            Message(MessagingPlatform.TikTok, Guid.NewGuid(), "open-id-1", false, DateTime.UtcNow, DateTime.UtcNow)));

        Assert.Equal(48, row.ReplyWindowHours);
    }

    [MongoFact]
    public async Task AConversationWithOnlyRepliesHasNoStartTime()
    {
        ConversationSummary row = Assert.Single(await ListAsync(
            Message(MessagingPlatform.WhatsApp, Guid.NewGuid(), "2348012345678", true, DateTime.UtcNow, DateTime.UtcNow)));

        Assert.Null(row.LastCustomerMessageAt);
    }
}
