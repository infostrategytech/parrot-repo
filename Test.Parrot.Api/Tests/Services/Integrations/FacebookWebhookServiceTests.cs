using Microsoft.Extensions.Options;
using Moq;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Integrations;
using Parrot.Application.Logging;
using Parrot.Application.Messaging;
using Parrot.Domain.Entities;
using Parrot.Domain.Enums;

namespace Test.Parrot.Api.Tests.Services.Integrations;

public class FacebookWebhookServiceTests
{
    private const string PageId = "5647382910";
    private const string Customer = "1029384756";
    private const string RawBody = "{\"object\":\"page\",\"entry\":[]}";

    private readonly Mock<IMessageBus> _busMock = new();
    private readonly Mock<IFacebookConnectionRepository> _connections = new();
    private readonly List<(string Topic, string? Key, InboundMessage Message)> _published = [];
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly FacebookWebhookService _sut;

    public FacebookWebhookServiceTests()
    {
        _busMock
            .Setup(b => b.PublishAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Callback<string, string?, InboundMessage, CancellationToken>((topic, key, message, _) => _published.Add((topic, key, message)))
            .Returns(Task.CompletedTask);

        _connections
            .Setup(c => c.GetActiveByIdentifierAsync("reference", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FacebookConnection
            {
                Id = _connectionId,
                WorkspaceId = "owner-1",
                VerifyTokenEncrypted = "encrypted",
                PageId = PageId,
                PageName = "Oma's Kitchen",
                WebhookVerifiedAt = DateTime.UtcNow,
            });

        _sut = new FacebookWebhookService(
            Options.Create(new FacebookSettings { AppSecret = "test-secret", WebhookBaseUrl = "https://api.example.com" }),
            _connections.Object,
            new InboundMessagePublisher(_busMock.Object, Options.Create(new KafkaSettings()), Mock.Of<IAppLogger<InboundMessagePublisher>>()),
            Mock.Of<IAppLogger<FacebookWebhookService>>(),
            Mock.Of<IFacebookCredentialProtector>());
    }

    [Fact]
    public async Task ProcessConnectionEventAsync_CustomerMessage_PublishesItKeyedByConversation()
    {
        FacebookWebhookPayload payload = Payload(new FacebookMessagingEvent
        {
            Sender = new FacebookParticipant { Id = Customer },
            Recipient = new FacebookParticipant { Id = PageId },
            Timestamp = 1700000000123,
            Message = new FacebookMessage { Mid = "m_abc", Text = "Hello" },
        });

        Assert.True(await _sut.ProcessConnectionEventAsync("reference", payload, RawBody));

        (string topic, string? key, InboundMessage message) = Assert.Single(_published);
        Assert.Equal("messages.inbound", topic);
        Assert.Equal($"facebook:{Customer}:{PageId}", key);
        Assert.Equal("facebook:m_abc", message.MessageId);
        Assert.Equal(MessagingPlatform.Facebook, message.Source);
        Assert.Equal(Customer, message.Sender);
        Assert.Equal(PageId, message.Receiver);
        Assert.Equal(RawBody, message.Data);
        Assert.Equal(new DateTime(2023, 11, 14, 22, 13, 20, 123, DateTimeKind.Utc), message.SentAt);
        Assert.Equal(_connectionId, message.ConnectionId);
        Assert.Equal("owner-1", message.OwnerUserId);
    }

    [Fact]
    public async Task ProcessConnectionEventAsync_Postback_IsPublishedAsAMessage()
    {
        FacebookWebhookPayload payload = Payload(new FacebookMessagingEvent
        {
            Sender = new FacebookParticipant { Id = Customer },
            Recipient = new FacebookParticipant { Id = PageId },
            Timestamp = 1700000000123,
            Postback = new FacebookPostback { Mid = "m_postback", Title = "Get Started", Payload = "GET_STARTED" },
        });

        Assert.True(await _sut.ProcessConnectionEventAsync("reference", payload, RawBody));

        Assert.Equal("facebook:m_postback", Assert.Single(_published).Message.MessageId);
    }

    // The Page's own replies come back as echoes; publishing them would store
    // the business's message as if the customer had sent it.
    [Fact]
    public async Task ProcessConnectionEventAsync_EchoAndReceipts_AreNotPublished()
    {
        FacebookWebhookPayload payload = Payload(
            new FacebookMessagingEvent
            {
                Sender = new FacebookParticipant { Id = PageId },
                Recipient = new FacebookParticipant { Id = Customer },
                Message = new FacebookMessage { Mid = "m_echo", Text = "Thanks!", IsEcho = true },
            },
            new FacebookMessagingEvent
            {
                Sender = new FacebookParticipant { Id = Customer },
                Recipient = new FacebookParticipant { Id = PageId },
                Delivery = new FacebookDelivery { Watermark = 1700000000000 },
            },
            new FacebookMessagingEvent
            {
                Sender = new FacebookParticipant { Id = Customer },
                Recipient = new FacebookParticipant { Id = PageId },
                Read = new FacebookRead { Watermark = 1700000000000 },
            });

        Assert.True(await _sut.ProcessConnectionEventAsync("reference", payload, RawBody));

        Assert.Empty(_published);
    }

    [Fact]
    public async Task ProcessConnectionEventAsync_PageMismatch_IsRejectedWithoutPublishing()
    {
        FacebookWebhookPayload payload = new()
        {
            Object = "page",
            Entry = [new FacebookEntry { Id = "another-page", Messaging = [] }],
        };

        Assert.False(await _sut.ProcessConnectionEventAsync("reference", payload, RawBody));
        Assert.Empty(_published);
    }

    private static FacebookWebhookPayload Payload(params FacebookMessagingEvent[] events) => new()
    {
        Object = "page",
        Entry = [new FacebookEntry { Id = PageId, Messaging = [.. events] }],
    };
}
