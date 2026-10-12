using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Moq;
using Parrot.Application.Conversations;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Integrations;
using Parrot.Application.Logging;
using Parrot.Application.Messaging;
using Parrot.Domain.Entities;
using Parrot.Domain.Entities.Messages;
using Parrot.Domain.Enums;
using Parrot.Infrastructure.Conversations;
using Parrot.Infrastructure.Messaging;
using static Test.Parrot.Api.Tests.Services.MongoConversationTests;

namespace Test.Parrot.Api.Tests.Services.Messaging;

// A real Instagram DM webhook, end to end over real MongoDB: the signature is checked with the
// connection's Instagram app secret, the worker stores each message once, the Channels test finds
// it, the inbox lists it as Instagram, and a reply joins the conversation in order.
public class InstagramPipelineTests : IAsyncLifetime
{
    private const string Owner = "owner-1";
    private const string AccountId = "17841400000000000";
    private const string Customer = "5834920000000000";
    private const string Secret = "fedcba9876543210fedcba9876543210";
    private static readonly Guid ConnectionId = Guid.NewGuid();

    // Now, so the customer's messages are always inside the 24-hour reply window.
    private static readonly long Now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // A text, an image, the business's own reply echoed back, a read receipt, and an icebreaker tap.
    private static readonly string Body = $$$"""
        {"object":"instagram","entry":[{"id":"{{{AccountId}}}","time":{{{Now}}},"messaging":[
          {"sender":{"id":"{{{Customer}}}"},"recipient":{"id":"{{{AccountId}}}"},"timestamp":{{{Now}}},
           "message":{"mid":"ig_text","text":"Is the blue one back in stock?"}},
          {"sender":{"id":"{{{Customer}}}"},"recipient":{"id":"{{{AccountId}}}"},"timestamp":{{{Now + 1000}}},
           "message":{"mid":"ig_image","attachments":[{"type":"image","payload":{"url":"https://cdn.example/b.jpg"}}]}},
          {"sender":{"id":"{{{AccountId}}}"},"recipient":{"id":"{{{Customer}}}"},"timestamp":{{{Now + 2000}}},
           "message":{"mid":"ig_echo","text":"Let me check","is_echo":true}},
          {"sender":{"id":"{{{Customer}}}"},"recipient":{"id":"{{{AccountId}}}"},"timestamp":{{{Now + 3000}}},
           "read":{"mid":"ig_echo"}},
          {"sender":{"id":"{{{Customer}}}"},"recipient":{"id":"{{{AccountId}}}"},"timestamp":{{{Now + 4000}}},
           "postback":{"mid":"ig_postback","title":"Prices","payload":"PRICES"}}
        ]}]}
        """;

    private readonly string _database = "parrot_test_" + Guid.NewGuid().ToString("N");
    private readonly List<InboundMessage> _published = [];
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

    private sealed class Protector : IInstagramCredentialProtector
    {
        public string Protect(string value) => $"enc:{value}";
        public string Unprotect(string protectedValue) => protectedValue[4..];
    }

    private static InstagramConnection Connection(DateTime? disabledAt = null) => new()
    {
        Id = ConnectionId, OwnerUserId = Owner, VerifyTokenEncrypted = "enc:verify", AccountId = AccountId,
        Username = "omaskitchen", WebhookVerifiedAt = DateTime.UtcNow, AccessTokenEncrypted = "enc:IGAAtoken",
        AppSecretEncrypted = $"enc:{Secret}", DisabledAt = disabledAt,
    };

    private static string Sign(string body, string secret) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    private InstagramWebhookService Webhooks(InstagramConnection? connection, WebhookSignatureMode mode = WebhookSignatureMode.Enforce)
    {
        Mock<IMessageBus> bus = new();
        bus.Setup(b => b.PublishAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Callback<string, string?, InboundMessage, CancellationToken>((_, _, message, _) => _published.Add(message))
            .Returns(Task.CompletedTask);
        Mock<IInstagramConnectionRepository> connections = new();
        connections.Setup(c => c.GetActiveByIdentifierAsync("ref", It.IsAny<CancellationToken>())).ReturnsAsync(connection);
        return new InstagramWebhookService(
            Options.Create(new InstagramSettings { AppSecret = "0123456789abcdef0123456789abcdef", SignatureMode = mode }),
            connections.Object,
            new InboundMessagePublisher(bus.Object, Options.Create(new KafkaSettings()), Mock.Of<IAppLogger<InboundMessagePublisher>>()),
            Mock.Of<IAppLogger<InstagramWebhookService>>(),
            new Protector());
    }

    private static FacebookWebhookPayload Payload(string body = "") =>
        JsonSerializer.Deserialize<FacebookWebhookPayload>(body.Length > 0 ? body : Body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private InboundMessageProcessor Worker()
    {
        MessageDocumentMapperFactory mappers = new([new WhatsAppMessageDocumentMapper(), new FacebookMessageDocumentMapper(), new TikTokMessageDocumentMapper(), new InstagramMessageDocumentMapper()]);
        _ = new MongoMessageRepository(_client!, Settings, mappers);
        return new InboundMessageProcessor(
            new InboundMessageDecoders([new WhatsAppInboundDecoder(), new FacebookInboundDecoder(), new InstagramInboundDecoder()]),
            new MongoStoredMessageStore(_client!, Settings),
            new MongoConversationSequence(_client!, Settings),
            mappers,
            Mock.Of<IConnectionMessageReceipt>(),
            Mock.Of<IMessagePersistedPublisher>(),
            NullLogger<InboundMessageProcessor>.Instance);
    }

    private async Task DeliverAsync()
    {
        InstagramWebhookService webhooks = Webhooks(Connection());
        Assert.True(await webhooks.IsAuthenticAsync("ref", Body, Sign(Body, Secret)));
        Assert.True(await webhooks.ProcessConnectionEventAsync("ref", Payload(), Body));
        InboundMessageProcessor worker = Worker();
        foreach (InboundMessage message in _published) await worker.ProcessAsync(message, CancellationToken.None);
    }

    // The Instagram app secret signs a manual connection's webhooks; Parrot's own secret must not pass for it.
    [Fact]
    public async Task TheSignature_IsCheckedWithTheConnectionsInstagramAppSecret()
    {
        InstagramWebhookService webhooks = Webhooks(Connection());

        Assert.True(await webhooks.IsAuthenticAsync("ref", Body, Sign(Body, Secret)));
        Assert.False(await webhooks.IsAuthenticAsync("ref", Body, Sign(Body, "0123456789abcdef0123456789abcdef")));
        Assert.False(await webhooks.IsAuthenticAsync("ref", Body, null));
        Assert.True(await Webhooks(Connection(), WebhookSignatureMode.LogOnly).IsAuthenticAsync("ref", Body, null));
    }

    [Fact]
    public async Task AnotherAccountsWebhook_OrADisconnectedOne_IsRefused()
    {
        string other = Body.Replace($"\"id\":\"{AccountId}\",\"time\"", "\"id\":\"17841499999999999\",\"time\"");

        Assert.False(await Webhooks(Connection()).ProcessConnectionEventAsync("ref", Payload(other), other));
        Assert.False(await Webhooks(null).ProcessConnectionEventAsync("ref", Payload(), Body));
        Assert.False(await Webhooks(Connection()).ProcessConnectionEventAsync("ref", Payload(Body.Replace("\"object\":\"instagram\"", "\"object\":\"page\"")), Body));
        Assert.Empty(_published);
    }

    [MongoFact]
    public async Task ADmWebhook_IsStoredOnceInOrder_AsOneInstagramConversation()
    {
        await DeliverAsync();
        // A redelivered batch stores nothing new.
        foreach (InboundMessage message in _published) await Worker().ProcessAsync(message, CancellationToken.None);

        // The echo and the read receipt never become messages.
        Assert.Equal(["instagram:ig_text", "instagram:ig_image", "instagram:ig_postback"], _published.Select(m => m.MessageId).Distinct());
        List<MessageDocument> stored = await Messages.Find(FilterDefinition<MessageDocument>.Empty).SortBy(d => d.Sequence).ToListAsync();
        Assert.All(stored, d => Assert.IsType<InstagramMessageDocument>(d));
        Assert.Equal([1L, 2L, 3L], stored.Select(d => d.Sequence!.Value));
        Assert.All(stored, d =>
        {
            Assert.Equal(ConversationKey.Create(MessagingPlatform.Instagram, Customer, AccountId), d.ConversationKey);
            Assert.Equal(ConnectionId, d.ConnectionId);
            Assert.Equal(Owner, d.OwnerUserId);
            Assert.Equal(Customer, d.From);
            Assert.Equal(AccountId, d.ChannelId);
            Assert.Equal(MessagingPlatform.Instagram, d.Platform);
        });
        Assert.StartsWith("instagram:", stored[0].ConversationKey);
        Assert.Equal("Is the blue one back in stock?", stored[0].TextBody);
        Assert.Equal(["https://cdn.example/b.jpg"], ((InstagramMessageDocument)stored[1]).AttachmentUrls!);
        Assert.Equal("PRICES", stored[2].TextBody);
    }

    [MongoFact]
    public async Task TheChannelsTest_AndTheInbox_SeeItAsInstagram()
    {
        DateTime since = DateTime.UtcNow.AddSeconds(-5);
        await DeliverAsync();

        MongoMessageRepository repository = new(_client!, Settings,
            new MessageDocumentMapperFactory([new WhatsAppMessageDocumentMapper(), new FacebookMessageDocumentMapper(), new TikTokMessageDocumentMapper(), new InstagramMessageDocumentMapper()]));
        Assert.Equal("instagram:ig_text", (await repository.FindFirstAsync(ConnectionId, Owner, since))?.MessageId);
        ConversationSummary summary = Assert.Single(await new MongoConversationStore(_client!, Settings).ListAsync(Owner, 0, 50, CancellationToken.None));
        Assert.Equal("instagram", summary.Channel);
        Assert.Equal(Customer, summary.Sender);
    }

    [MongoFact]
    public async Task AReplyFromTheInbox_GoesOutThroughTheAccount_AndJoinsTheConversationInOrder()
    {
        await DeliverAsync();
        Mock<IInstagramConnectionRepository> accounts = new();
        accounts.Setup(a => a.GetByOwnerAsync(Owner, It.IsAny<CancellationToken>())).ReturnsAsync(Connection());
        Mock<IInstagramSender> instagram = new();
        instagram.Setup(i => i.SendAsync(AccountId, "IGAAtoken", Customer, "Yes, back in stock today.", It.IsAny<CancellationToken>()))
            .ReturnsAsync("aWdfreply");
        ConversationService service = new(new MongoConversationStore(_client!, Settings), Mock.Of<IWhatsAppConnectionRepository>(),
            Mock.Of<IWhatsAppSender>(), Mock.Of<IWhatsAppTokenProtector>(), new MongoConversationSequence(_client!, Settings),
            Mock.Of<IFacebookConnectionRepository>(), Mock.Of<IFacebookSender>(), Mock.Of<IFacebookCredentialProtector>(),
            accounts.Object, instagram.Object, new Protector());

        ConversationMessage reply = await service.SendAsync(Owner, ConnectionId, Customer,
            new SendReplyRequest(Guid.NewGuid(), "Yes, back in stock today."), CancellationToken.None);

        Assert.Equal("accepted", reply.Status);
        MessageHistoryPage history = await new MongoMessageHistory(_client!, Settings)
            .GetAfterAsync(Owner, ConversationKey.Create(MessagingPlatform.Instagram, Customer, AccountId), 0, 50);
        Assert.Equal([1L, 2L, 3L, 4L], history.Messages.Select(m => m.Sequence));
        StoredMessage last = history.Messages[^1];
        Assert.True(last.Outgoing);
        Assert.Equal(AccountId, last.Sender);
        Assert.Equal(Customer, last.Receiver);
        Assert.Equal(MessagingPlatform.Instagram, last.Platform);
    }

    // Instagram counts UTF-8 bytes: 1000 letters fit, 400 emoji (4 bytes each) do not.
    [Fact]
    public async Task AReplyOverInstagramsByteLimit_IsRefusedBeforeSending()
    {
        Mock<IInstagramConnectionRepository> accounts = new();
        accounts.Setup(a => a.GetByOwnerAsync(Owner, It.IsAny<CancellationToken>())).ReturnsAsync(Connection());
        Mock<IInstagramSender> instagram = new();
        ConversationService service = new(Mock.Of<IConversationStore>(), Mock.Of<IWhatsAppConnectionRepository>(),
            Mock.Of<IWhatsAppSender>(), Mock.Of<IWhatsAppTokenProtector>(), Mock.Of<IConversationSequence>(),
            Mock.Of<IFacebookConnectionRepository>(), Mock.Of<IFacebookSender>(), Mock.Of<IFacebookCredentialProtector>(),
            accounts.Object, instagram.Object, new Protector());

        await Assert.ThrowsAsync<global::Parrot.Domain.Exceptions.ValidationException>(() =>
            service.SendAsync(Owner, ConnectionId, Customer, new SendReplyRequest(Guid.NewGuid(), string.Concat(Enumerable.Repeat("😀", 400))), default));
        instagram.VerifyNoOtherCalls();
    }
}
