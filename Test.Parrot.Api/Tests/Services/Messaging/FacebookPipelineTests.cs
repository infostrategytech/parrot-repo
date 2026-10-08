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

// A real Messenger webhook, end to end over real MongoDB: the webhook publishes, the message
// worker decodes and stores each message once, the Channels test finds it, and the inbox
// lists it as a Facebook conversation.
public class FacebookPipelineTests : IAsyncLifetime
{
    private const string Owner = "owner-1";
    private const string PageId = "185860297939218";
    private const string Customer = "6215837091842310";
    private static readonly Guid ConnectionId = Guid.NewGuid();

    // A text, an image, the Page's own reply echoed back, and a button tap.
    private static readonly string Body = $$$"""
        {"object":"page","entry":[{"id":"{{{PageId}}}","time":1791447800000,"messaging":[
          {"sender":{"id":"{{{Customer}}}"},"recipient":{"id":"{{{PageId}}}"},"timestamp":1791447800000,
           "message":{"mid":"m_text","text":"Hello, I want to know more about your products."}},
          {"sender":{"id":"{{{Customer}}}"},"recipient":{"id":"{{{PageId}}}"},"timestamp":1791447801000,
           "message":{"mid":"m_image","attachments":[{"type":"image","payload":{"url":"https://cdn.example/a.jpg"}}]}},
          {"sender":{"id":"{{{PageId}}}"},"recipient":{"id":"{{{Customer}}}"},"timestamp":1791447802000,
           "message":{"mid":"m_echo","text":"Thanks for writing!","is_echo":true}},
          {"sender":{"id":"{{{Customer}}}"},"recipient":{"id":"{{{PageId}}}"},"timestamp":1791447803000,
           "postback":{"mid":"m_postback","title":"See prices","payload":"SEE_PRICES"}}
        ]}]}
        """;

    private readonly string _database = "parrot_test_" + Guid.NewGuid().ToString("N");
    private readonly List<InboundMessage> _published = [];
    private readonly List<MessagePersisted> _persisted = [];
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

    private sealed class Persisted(List<MessagePersisted> sink) : IMessagePersistedPublisher
    {
        public Task PublishAsync(MessagePersisted persisted, CancellationToken cancellationToken = default)
        {
            sink.Add(persisted);
            return Task.CompletedTask;
        }
    }

    private async Task<IReadOnlyList<InboundMessage>> ReceiveWebhookAsync()
    {
        Mock<IMessageBus> bus = new();
        bus.Setup(b => b.PublishAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Callback<string, string?, InboundMessage, CancellationToken>((_, _, message, _) => _published.Add(message))
            .Returns(Task.CompletedTask);
        Mock<IFacebookConnectionRepository> connections = new();
        connections.Setup(c => c.GetActiveByIdentifierAsync("ref", It.IsAny<CancellationToken>())).ReturnsAsync(new FacebookConnection
        {
            Id = ConnectionId,
            OwnerUserId = Owner,
            VerifyTokenEncrypted = "encrypted",
            PageId = PageId,
            PageName = "Oma's Kitchen",
            WebhookVerifiedAt = DateTime.UtcNow,
        });
        FacebookWebhookService webhooks = new(
            Options.Create(new FacebookSettings()),
            connections.Object,
            new InboundMessagePublisher(bus.Object, Options.Create(new KafkaSettings()), Mock.Of<IAppLogger<InboundMessagePublisher>>()),
            Mock.Of<IAppLogger<FacebookWebhookService>>(),
            Mock.Of<IFacebookCredentialProtector>());

        FacebookWebhookPayload payload = JsonSerializer.Deserialize<FacebookWebhookPayload>(Body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(await webhooks.ProcessConnectionEventAsync("ref", payload, Body));
        return _published;
    }

    private InboundMessageProcessor Worker()
    {
        _ = new MongoMessageRepository(_client!, Settings,
            new MessageDocumentMapperFactory([new WhatsAppMessageDocumentMapper(), new FacebookMessageDocumentMapper()]));
        return new InboundMessageProcessor(
            new InboundMessageDecoders([new WhatsAppInboundDecoder(), new FacebookInboundDecoder()]),
            new MongoStoredMessageStore(_client!, Settings),
            new MongoConversationSequence(_client!, Settings),
            new MessageDocumentMapperFactory([new WhatsAppMessageDocumentMapper(), new FacebookMessageDocumentMapper()]),
            Mock.Of<IConnectionMessageReceipt>(),
            new Persisted(_persisted),
            NullLogger<InboundMessageProcessor>.Instance);
    }

    [MongoFact]
    public async Task AMessengerWebhook_IsStoredOnceInOrder_AsOneFacebookConversation()
    {
        IReadOnlyList<InboundMessage> inbound = await ReceiveWebhookAsync();
        InboundMessageProcessor worker = Worker();
        foreach (InboundMessage message in inbound) await worker.ProcessAsync(message, CancellationToken.None);
        // A redelivered batch stores nothing new.
        foreach (InboundMessage message in inbound) await worker.ProcessAsync(message, CancellationToken.None);

        // The Page's own echo never becomes a message.
        Assert.Equal(["facebook:m_text", "facebook:m_image", "facebook:m_postback"], inbound.Select(m => m.MessageId));
        List<MessageDocument> stored = await Messages.Find(FilterDefinition<MessageDocument>.Empty).SortBy(d => d.Sequence).ToListAsync();
        Assert.All(stored, d => Assert.IsType<FacebookMessageDocument>(d));
        Assert.Equal([1L, 2L, 3L], stored.Select(d => d.Sequence!.Value));
        Assert.Single(stored.Select(d => d.ConversationKey).Distinct());
        Assert.Equal(ConversationKey.Create(MessagingPlatform.Facebook, Customer, PageId), stored[0].ConversationKey);
        Assert.All(stored, d =>
        {
            Assert.Equal(ConnectionId, d.ConnectionId);
            Assert.Equal(Owner, d.OwnerUserId);
            Assert.Equal(Customer, d.From);
            Assert.Equal(PageId, d.ChannelId);
        });

        Assert.Equal("Hello, I want to know more about your products.", stored[0].TextBody);
        Assert.Equal("image", stored[1].Type);
        Assert.Equal(["https://cdn.example/a.jpg"], ((FacebookMessageDocument)stored[1]).AttachmentUrls!);
        Assert.Equal("postback", stored[2].Type);
        Assert.Equal("SEE_PRICES", stored[2].TextBody);

        // Each delivery announces the stored copy for live delivery, to the Page's owner.
        MessagePersisted first = _persisted[0];
        Assert.Equal(MessagingPlatform.Facebook, first.Source);
        Assert.Equal(Owner, first.Data.OwnerUserId);
        Assert.Equal(Customer, first.Data.Sender);
        Assert.Equal(PageId, first.Data.Receiver);
    }

    // The Channels "send a test message" step: by Parrot's connection id and owner.
    [MongoFact]
    public async Task TheChannelsTest_FindsTheFirstCustomerMessage_OnlyForItsOwner()
    {
        DateTime since = DateTime.UtcNow.AddSeconds(-5);
        InboundMessageProcessor worker = Worker();
        foreach (InboundMessage message in await ReceiveWebhookAsync()) await worker.ProcessAsync(message, CancellationToken.None);
        MongoMessageRepository repository = new(_client!, Settings,
            new MessageDocumentMapperFactory([new WhatsAppMessageDocumentMapper(), new FacebookMessageDocumentMapper()]));

        ReceivedMessageSummary? found = await repository.FindFirstAsync(ConnectionId, Owner, since);

        Assert.Equal("facebook:m_text", found?.MessageId);
        Assert.Null(await repository.FindFirstAsync(ConnectionId, "someone-else", since));
        Assert.Null(await repository.FindFirstAsync(Guid.NewGuid(), Owner, since));
        Assert.Null(await repository.FindFirstAsync(ConnectionId, Owner, DateTime.UtcNow.AddMinutes(1)));
    }

    // A reply sent while the test waits is not "a message arrived".
    [MongoFact]
    public async Task TheChannelsTest_IgnoresTheBusinessesOwnReplies()
    {
        DateTime since = DateTime.UtcNow.AddSeconds(-5);
        MongoMessageRepository repository = new(_client!, Settings,
            new MessageDocumentMapperFactory([new WhatsAppMessageDocumentMapper(), new FacebookMessageDocumentMapper()]));
        await Messages.InsertOneAsync(new FacebookMessageDocument
        {
            MessageId = "outgoing:reply", Platform = MessagingPlatform.Facebook, ExternalAccountId = PageId,
            ChannelId = PageId, ChannelDisplayName = "", From = Customer, Timestamp = "0", Type = "text",
            TextBody = "We'll be in touch", OwnerUserId = Owner, ConnectionId = ConnectionId,
            Outgoing = true, ReceivedAt = DateTime.UtcNow, StoredAt = DateTime.UtcNow,
        });

        Assert.Null(await repository.FindFirstAsync(ConnectionId, Owner, since));
    }

    [MongoFact]
    public async Task TheInbox_ListsItAsAFacebookConversation()
    {
        InboundMessageProcessor worker = Worker();
        foreach (InboundMessage message in await ReceiveWebhookAsync()) await worker.ProcessAsync(message, CancellationToken.None);

        ConversationSummary summary = Assert.Single(await new MongoConversationStore(_client!, Settings).ListAsync(Owner, 0, 50, CancellationToken.None));

        Assert.Equal("facebook", summary.Channel);
        Assert.Equal(ConnectionId, summary.ConnectionId);
        Assert.Equal(Customer, summary.Sender);
    }
}
