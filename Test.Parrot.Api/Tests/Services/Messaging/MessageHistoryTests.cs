using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Parrot.Api.Controllers;
using Microsoft.Extensions.Options;
using Moq;
using Parrot.Application.Conversations;
using MongoDB.Bson;
using MongoDB.Driver;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;
using Parrot.Application.Teams;
using Parrot.Domain.Entities.Messages;
using Parrot.Domain.Entities;
using Parrot.Domain.Enums;
using Parrot.Infrastructure.Conversations;
using Parrot.Infrastructure.Messaging;
using Parrot.Infrastructure.SocialMediaIntegrations;
using static Test.Parrot.Api.Tests.Services.MongoConversationTests;

namespace Test.Parrot.Api.Tests.Services.Messaging;

public class MessageHistoryTests : IAsyncLifetime
{
    private const string Owner = "owner-1";
    private const string Ada = "whatsapp:2348012345678:2349011112222";
    private const string Tunde = "whatsapp:2348033334444:2349011112222";

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

    private IMongoCollection<MessageDocument> Messages =>
        _client!.GetDatabase(_database).GetCollection<MessageDocument>("messages");

    private MongoMessageHistory History() =>
        new(_client!, Options.Create(new MongoDbSettings { Database = _database, Collection = "messages" }));

    [MongoFact]
    public async Task ReturnsTheMessagesAfterTheCursor_OldestFirst()
    {
        MongoMessageHistory history = History();
        // Sequences can skip numbers; 3 and 4 were never stored.
        await Store(Ada, Owner, 5, 1, 2);

        MessageHistoryPage page = await history.GetAfterAsync(Owner, Ada, afterSequence: 1, limit: 50);

        Assert.Equal([2, 5], page.Messages.Select(m => m.Sequence));
        Assert.Null(page.NextCursor);
    }

    [MongoFact]
    public async Task PagesThroughAConversation_WithTheCursorItReturns()
    {
        MongoMessageHistory history = History();
        await Store(Ada, Owner, 1, 2, 3, 4, 5);

        MessageHistoryPage first = await history.GetAfterAsync(Owner, Ada, 0, limit: 2);
        MessageHistoryPage second = await history.GetAfterAsync(Owner, Ada, first.NextCursor!.Value, limit: 2);
        MessageHistoryPage last = await history.GetAfterAsync(Owner, Ada, second.NextCursor!.Value, limit: 2);

        Assert.Equal([1, 2], first.Messages.Select(m => m.Sequence));
        Assert.Equal(2, first.NextCursor);
        Assert.Equal([3, 4], second.Messages.Select(m => m.Sequence));
        Assert.Equal(4, second.NextCursor);
        Assert.Equal([5], last.Messages.Select(m => m.Sequence));
        Assert.Null(last.NextCursor);
    }

    // A conversation key is two phone numbers, so it is easy to guess.
    [MongoFact]
    public async Task AnotherUsersConversation_ReadsAsEmpty()
    {
        MongoMessageHistory history = History();
        await Store(Ada, Owner, 1, 2);

        MessageHistoryPage page = await history.GetAfterAsync("owner-2", Ada, 0, limit: 50);

        Assert.Empty(page.Messages);
        Assert.Null(page.NextCursor);
    }

    [MongoFact]
    public async Task LeavesOutOtherConversations_AndMessagesWithoutASequence()
    {
        MongoMessageHistory history = History();
        await Store(Ada, Owner, 1);
        await Store(Tunde, Owner, 1, 2);

        // An outgoing reply: ConversationService stores it without a sequence.
        await Messages.InsertOneAsync(Document("reply", Ada, Owner, sequence: null, outgoing: true));

        MessageHistoryPage page = await history.GetAfterAsync(Owner, Ada, 0, limit: 50);

        StoredMessage message = Assert.Single(page.Messages);
        Assert.Equal(Ada, message.ConversationKey);
        Assert.Equal(1, message.Sequence);
    }

    [MongoFact]
    public async Task CapsThePageSize()
    {
        MongoMessageHistory history = History();
        await Store(Ada, Owner, Enumerable.Range(1, MongoMessageHistory.MaxPageSize + 5).Select(n => (long)n).ToArray());

        MessageHistoryPage page = await history.GetAfterAsync(Owner, Ada, 0, limit: 1000);

        Assert.Equal(MongoMessageHistory.MaxPageSize, page.Messages.Count);
        Assert.Equal(MongoMessageHistory.MaxPageSize, page.NextCursor);
    }

    [MongoFact]
    public async Task MapsTheStoredDocumentToTheSharedShape()
    {
        MongoMessageHistory history = History();
        await Store(Ada, Owner, 7);

        StoredMessage message = Assert.Single((await history.GetAfterAsync(Owner, Ada, 0, limit: 50)).Messages);

        Assert.Equal("whatsapp:wamid.7", message.MessageId);
        Assert.Equal(MessagingPlatform.WhatsApp, message.Platform);
        Assert.Equal(Owner, message.OwnerUserId);
        Assert.Equal("2348012345678", message.Sender);
        Assert.Equal("2349011112222", message.Receiver);
        Assert.Equal("Ada", message.SenderName);
        Assert.Equal("Message 7", message.Text);
        Assert.False(message.Outgoing);
    }

    // The query GetAfterAsync sends must be one index range scan, already in
    // order: no collection scan and no in-memory sort.
    [MongoFact]
    public async Task TheCursorQueryUsesTheHistoryIndex()
    {
        History();
        await Store(Ada, Owner, 1, 2, 3);

        BsonDocument explain = await _client!.GetDatabase(_database).RunCommandAsync<BsonDocument>(new BsonDocument("explain", new BsonDocument
        {
            { "find", "messages" },
            { "filter", new BsonDocument { { "OwnerUserId", Owner }, { "ConversationKey", Ada }, { "Sequence", new BsonDocument("$gt", 1L) } } },
            { "sort", new BsonDocument("Sequence", 1) },
            { "limit", 3 },
        }));
        string plan = explain["queryPlanner"]["winningPlan"].ToJson();

        Assert.Contains(MongoMessageHistory.IndexName, plan);
        Assert.DoesNotContain("COLLSCAN", plan);
        Assert.DoesNotContain("\"SORT\"", plan);
    }

    // Ada writes, the business replies through the inbox, Ada writes again:
    // history returns all three in order, the reply sent by the business.
    [MongoFact]
    public async Task ABusinessReply_SitsInTheConversationBetweenTheCustomersMessages()
    {
        IOptions<MongoDbSettings> settings = Options.Create(new MongoDbSettings { Database = _database, Collection = "messages" });
        MongoConversationSequence counter = new(_client!, settings);
        MongoMessageHistory history = History();
        WhatsAppConnection connection = new()
        {
            WorkspaceId = Owner, Name = "Support", VerifyTokenEncrypted = "encrypted", WebhookVerifiedAt = DateTime.UtcNow,
            PhoneNumberId = "106540352242922", WhatsAppBusinessAccountId = "102290129340398",
            // Typed at setup in a different shape from Meta's; the reply must not depend on it.
            DisplayPhoneNumber = "0901 111 2222",
        };
        WhatsAppTokenProtector protector = new(new EphemeralDataProtectionProvider());
        connection.AccessTokenEncrypted = protector.Protect("token");
        Mock<IWhatsAppConnectionRepository> connections = new();
        connections.Setup(c => c.GetByOwnerAndIdAsync(Owner, connection.Id)).ReturnsAsync(connection);
        Mock<IWhatsAppSender> sender = new();
        sender.Setup(s => s.SendAsync(It.IsAny<string>(), It.IsAny<string>(), "2348012345678", "Try 1234", It.IsAny<CancellationToken>()))
            .ReturnsAsync("wamid.reply");
        ConversationService service = new(new MongoConversationStore(_client!, settings), connections.Object, sender.Object, protector, counter,
            Mock.Of<IFacebookConnectionRepository>(), Mock.Of<IFacebookSender>(), Mock.Of<IFacebookCredentialProtector>(),
            Mock.Of<IInstagramConnectionRepository>(), Mock.Of<IInstagramSender>(), Mock.Of<IInstagramCredentialProtector>());

        await StoreIncoming("wamid.first", connection.Id, await counter.NextAsync(Ada), DateTime.UtcNow.AddMinutes(-2));
        await service.SendAsync(Owner, connection.Id, "2348012345678", new SendReplyRequest(Guid.NewGuid(), "Try 1234"), default);
        await StoreIncoming("wamid.second", connection.Id, await counter.NextAsync(Ada), DateTime.UtcNow);

        MessageHistoryPage page = await history.GetAfterAsync(Owner, Ada, 0, limit: 50);

        Assert.Equal([1, 2, 3], page.Messages.Select(m => m.Sequence));
        StoredMessage reply = page.Messages[1];
        Assert.True(reply.Outgoing);
        Assert.Equal("Try 1234", reply.Text);
        Assert.Equal("2349011112222", reply.Sender);
        Assert.Equal("2348012345678", reply.Receiver);
        Assert.False(page.Messages[0].Outgoing);
        Assert.Equal("2348012345678", page.Messages[0].Sender);
    }

    private Task StoreIncoming(string providerId, Guid connectionId, long sequence, DateTime at)
    {
        WhatsAppMessageDocument document = Document(providerId, Ada, Owner, sequence, outgoing: false);
        document.ConnectionId = connectionId;
        document.SentAt = at;
        document.ReceivedAt = at;
        return Messages.InsertOneAsync(document);
    }

    // The recovery endpoint end to end over real MongoDB: a client pages with
    // the cursor each response gives it, and reads only its own messages.
    [MongoFact]
    public async Task TheRecoveryEndpoint_PagesTheSignedInUsersConversation()
    {
        await Store(Ada, Owner, 1, 2, 4);
        await Store(Ada, "owner-2", 3);

        using JsonDocument first = await GetHistory(Owner, after: 0, limit: 2);
        long cursor = first.RootElement.GetProperty("nextCursor").GetInt64();
        using JsonDocument second = await GetHistory(Owner, cursor, limit: 2);
        using JsonDocument stranger = await GetHistory("owner-3", after: 0, limit: 50);

        Assert.Equal([1, 2], Sequences(first));
        Assert.Equal("whatsapp", first.RootElement.GetProperty("messages")[0].GetProperty("platform").GetString());
        Assert.Equal([4], Sequences(second));
        Assert.Equal(JsonValueKind.Null, second.RootElement.GetProperty("nextCursor").ValueKind);
        Assert.Empty(Sequences(stranger));
    }

    private async Task<JsonDocument> GetHistory(string userId, long after, int limit)
    {
        MessageHistoryController controller = new(History())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test")),
                    // As WorkspaceAccessFilter leaves it: the owner, who reaches every channel.
                    Items = { [typeof(WorkspaceAccess)] = WorkspaceAccess.ForOwner(new Workspace { Id = userId ?? "none", OwnerId = userId ?? "none" }) },
                },
            },
        };
        JsonResult result = Assert.IsType<JsonResult>(await controller.Get(Ada, after, limit));
        return JsonDocument.Parse(JsonSerializer.Serialize(result.Value, (JsonSerializerOptions)result.SerializerSettings!));
    }

    private static long[] Sequences(JsonDocument page) =>
        page.RootElement.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("sequence").GetInt64()).ToArray();

    private Task Store(string conversationKey, string owner, params long[] sequences) =>
        Messages.InsertManyAsync(sequences.Select(n => Document($"wamid.{n}", conversationKey, owner, n, outgoing: false)));

    private static WhatsAppMessageDocument Document(string providerId, string conversationKey, string owner, long? sequence, bool outgoing) => new()
    {
        MessageId = $"whatsapp:{providerId}",
        Platform = MessagingPlatform.WhatsApp,
        ExternalAccountId = "102290129340398",
        ChannelId = "106540352242922",
        ChannelDisplayName = "+234 901 111 2222",
        From = conversationKey.Split(':')[1],
        Receiver = "2349011112222",
        ContactName = "Ada",
        Timestamp = "1790971900",
        Type = "text",
        TextBody = $"Message {sequence}",
        OwnerUserId = owner,
        ConnectionId = Guid.Parse("6f1c2d3e-4a5b-4c6d-8e7f-0a1b2c3d4e5f"),
        ConversationKey = conversationKey,
        Sequence = sequence,
        Outgoing = outgoing,
        StoredAt = new DateTime(2026, 10, 4, 9, 0, 0, DateTimeKind.Utc),
    };
}
