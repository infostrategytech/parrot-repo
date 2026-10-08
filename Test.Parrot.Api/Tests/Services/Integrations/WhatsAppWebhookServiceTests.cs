using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Moq;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Integrations;
using Parrot.Application.Logging;
using Parrot.Application.Messaging;
using Parrot.Domain.Entities;
using Parrot.Domain.Enums;
using Parrot.Domain.Exceptions;

namespace Test.Parrot.Api.Tests.Services.Integrations;

public class WhatsAppWebhookServiceTests
{
    private const string RawBody = "{\"object\":\"whatsapp_business_account\",\"entry\":[]}";

    private readonly Mock<IMessageBus> _busMock;
    private readonly Mock<IAppLogger<WhatsAppWebhookService>> _loggerMock;
    private readonly Mock<IWhatsAppConnectionRepository> _connectionRepositoryMock;
    private readonly List<(string Topic, string? Key, InboundMessage Message)> _published = [];
    private readonly WhatsAppWebhookService _sut;

    public WhatsAppWebhookServiceTests()
    {
        _busMock = new Mock<IMessageBus>();
        _busMock
            .Setup(b => b.PublishAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Callback<string, string?, InboundMessage, CancellationToken>((topic, key, message, _) => _published.Add((topic, key, message)))
            .Returns(Task.CompletedTask);

        _loggerMock = new Mock<IAppLogger<WhatsAppWebhookService>>();
        _connectionRepositoryMock = new Mock<IWhatsAppConnectionRepository>();

        _loggerMock
            .Setup(l => l.BeginEntityScope(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Mock.Of<IDisposable>());

        IOptions<WhatsAppSettings> settings = Options.Create(new WhatsAppSettings
        {
            WebhookBaseUrl = "https://api.example.com",
            VerifyToken = "test-verify-token",
            AppSecret = "test-secret",
        });

        InboundMessagePublisher publisher = new(
            _busMock.Object,
            Options.Create(new KafkaSettings()),
            Mock.Of<IAppLogger<InboundMessagePublisher>>());

        _sut = new WhatsAppWebhookService(
            settings,
            _loggerMock.Object,
            publisher,
            _connectionRepositoryMock.Object,
            Mock.Of<IWhatsAppTokenProtector>());
    }

    [Fact]
    public void GetWebhookUrl_ReturnsUrlWithTrailingSlashStripped()
    {
        string url = _sut.GetWebhookUrl();

        Assert.Equal("https://api.example.com/api/webhooks/whatsapp", url);
    }

    [Fact]
    public void TryVerifyChallenge_ValidModeAndToken_ReturnsTrueAndEchoesChallenge()
    {
        bool result = _sut.TryVerifyChallenge("subscribe", "test-verify-token", "challenge-xyz", out string echo);

        Assert.True(result);
        Assert.Equal("challenge-xyz", echo);
    }

    [Fact]
    public void TryVerifyChallenge_WrongMode_ReturnsFalseAndEmptyEcho()
    {
        bool result = _sut.TryVerifyChallenge("unsubscribe", "test-verify-token", "challenge-xyz", out string echo);

        Assert.False(result);
        Assert.Empty(echo);
    }

    [Fact]
    public void TryVerifyChallenge_WrongToken_ReturnsFalseAndEmptyEcho()
    {
        bool result = _sut.TryVerifyChallenge("subscribe", "wrong-token", "challenge-xyz", out string echo);

        Assert.False(result);
        Assert.Empty(echo);
    }

    [Fact]
    public async Task ProcessConnectionEventAsync_ValidConnection_AddsOwnershipToPublishedMessage()
    {
        Guid connectionId = Guid.NewGuid();
        _connectionRepositoryMock.Setup(r => r.GetActiveByIdentifierAsync("reference")).ReturnsAsync(NewConnection(connectionId));
        WhatsAppWebhookPayload payload = BuildPayload(
            "business-account-1", "phone-123", "+1 555 000 9999", [("15550001111", "msg-001", "text", "Hello")]);

        Assert.True(await _sut.ProcessConnectionEventAsync("reference", payload, RawBody));

        InboundMessage message = Assert.Single(_published).Message;
        Assert.Equal(connectionId, message.ConnectionId);
        Assert.Equal("owner-1", message.OwnerUserId);
    }

    [Theory]
    [InlineData("other-business", "phone-123")]
    [InlineData("business-account-1", "other-phone")]
    public async Task ProcessConnectionEventAsync_AccountMismatch_IsRejected(string businessId, string phoneId)
    {
        _connectionRepositoryMock.Setup(r => r.GetActiveByIdentifierAsync("reference")).ReturnsAsync(NewConnection(Guid.NewGuid()));
        WhatsAppWebhookPayload payload = BuildPayload(
            businessId, phoneId, "+1", [("15550001111", "msg-001", "text", "Hello")]);

        Assert.False(await _sut.ProcessConnectionEventAsync("reference", payload, RawBody));
        Assert.Empty(_published);
    }

    [Fact]
    public async Task ProcessEventAsync_TextMessage_PublishesToTheInboundTopicKeyedByConversation()
    {
        WhatsAppWebhookPayload payload = BuildPayload(
            entryId: "business-account-1",
            phoneNumberId: "phone-123",
            displayPhone: "+1 555 000 9999",
            messages: [("15550001111", "msg-001", "text", "Hello!")]);

        await _sut.ProcessEventAsync(payload, RawBody);

        (string topic, string? key, InboundMessage message) = Assert.Single(_published);
        Assert.Equal("messages.inbound", topic);
        Assert.Equal("whatsapp:15550001111:15550009999", key);
        Assert.Equal(key, message.ConversationKey);
        Assert.Equal("whatsapp:msg-001", message.MessageId);
        Assert.Equal(MessagingPlatform.WhatsApp, message.Source);
        Assert.Equal("15550001111", message.Sender);
        Assert.Equal("15550009999", message.Receiver);
        Assert.Equal(new DateTime(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc), message.SentAt);
    }

    [Fact]
    public async Task ProcessEventAsync_CarriesTheRawBodyUnchanged()
    {
        WhatsAppWebhookPayload payload = BuildPayload(
            "acct-1", "phone-1", "+1 555 000 9999", [("15550001111", "msg-001", "text", "Hi")]);

        await _sut.ProcessEventAsync(payload, RawBody);

        Assert.Equal(RawBody, Assert.Single(_published).Message.Data);
    }

    [Fact]
    public async Task ProcessEventAsync_CarriesTheRequestTraceAsCorrelationId()
    {
        using Activity request = new Activity("webhook.request").Start();
        WhatsAppWebhookPayload payload = BuildPayload(
            "acct-1", "phone-1", "+1 555 000 9999", [("15550001111", "msg-001", "text", "Hi")]);

        await _sut.ProcessEventAsync(payload, RawBody);

        Assert.Equal(request.TraceId.ToHexString(), Assert.Single(_published).Message.CorrelationId);
    }

    [Fact]
    public async Task ProcessEventAsync_MultipleMessages_PublishesEachInTheOrderSent()
    {
        WhatsAppWebhookPayload payload = BuildPayload(
            entryId: "business-account-1",
            phoneNumberId: "phone-123",
            displayPhone: "+1 555 000 9999",
            messages:
            [
                ("15550001111", "msg-001", "text", "Hello!"),
                ("15550002222", "msg-002", "text", "World!"),
            ]);

        await _sut.ProcessEventAsync(payload, RawBody);

        Assert.Equal(["whatsapp:msg-001", "whatsapp:msg-002"], _published.Select(p => p.Message.MessageId));
        Assert.NotEqual(_published[0].Key, _published[1].Key);
    }

    [Fact]
    public async Task ProcessEventAsync_TwoChangesWithMessages_PublishesBoth()
    {
        WhatsAppWebhookPayload payload = new WhatsAppWebhookPayload
        {
            Object = "whatsapp_business_account",
            Entry =
            [
                new WhatsAppEntry
                {
                    Id = "acct-1",
                    Changes =
                    [
                        BuildChange("phone-1", "+1", [("111", "msg-1", "text", "Hi")]),
                        BuildChange("phone-2", "+2", [("222", "msg-2", "text", "Hey")]),
                    ],
                },
            ],
        };

        await _sut.ProcessEventAsync(payload, RawBody);

        Assert.Equal(2, _published.Count);
    }

    [Fact]
    public async Task ProcessEventAsync_NonMessageField_DoesNotPublish()
    {
        WhatsAppWebhookPayload payload = new WhatsAppWebhookPayload
        {
            Object = "whatsapp_business_account",
            Entry =
            [
                new WhatsAppEntry
                {
                    Id = "acct-1",
                    Changes =
                    [
                        new WhatsAppChange
                        {
                            Field = "account_review_update",
                            Value = new WhatsAppChangeValue
                            {
                                MessagingProduct = "whatsapp",
                                Metadata = new WhatsAppMetadata
                                {
                                    PhoneNumberId = "phone-1",
                                    DisplayPhoneNumber = "+1",
                                },
                            },
                        },
                    ],
                },
            ],
        };

        await _sut.ProcessEventAsync(payload, RawBody);

        Assert.Empty(_published);
    }

    [Fact]
    public async Task ProcessEventAsync_StatusesOnly_DoesNotPublish()
    {
        WhatsAppWebhookPayload payload = new WhatsAppWebhookPayload
        {
            Object = "whatsapp_business_account",
            Entry =
            [
                new WhatsAppEntry
                {
                    Id = "acct-1",
                    Changes =
                    [
                        new WhatsAppChange
                        {
                            Field = "messages",
                            Value = new WhatsAppChangeValue
                            {
                                MessagingProduct = "whatsapp",
                                Metadata = new WhatsAppMetadata
                                {
                                    PhoneNumberId = "phone-1",
                                    DisplayPhoneNumber = "+1",
                                },
                                Statuses =
                                [
                                    new WhatsAppMessageStatus
                                    {
                                        Id = "msg-001",
                                        Status = "delivered",
                                        Timestamp = "1700000000",
                                        RecipientId = "15550001111",
                                    },
                                ],
                            },
                        },
                    ],
                },
            ],
        };

        await _sut.ProcessEventAsync(payload, RawBody);

        Assert.Empty(_published);
    }

    // Meta's shape for a reply it could not deliver, with fields we do not read.
    private const string FailedStatusBody = """
        {"object":"whatsapp_business_account","entry":[{"id":"acct-1","changes":[{"field":"messages","value":{
          "messaging_product":"whatsapp",
          "metadata":{"display_phone_number":"+1","phone_number_id":"phone-1"},
          "statuses":[{"id":"wamid.F","status":"failed","timestamp":"1700000000","recipient_id":"15550001111",
            "errors":[{"code":131047,"title":"Re-engagement message","message":"Re-engagement message",
              "error_data":{"details":"Message failed to send because more than 24 hours have passed since the customer last replied to this number."},
              "href":"https://developers.facebook.com/docs/whatsapp/cloud-api/support/error-codes/"}]}]
          }}]}]}
        """;

    private static WhatsAppWebhookPayload Bind(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<WhatsAppWebhookPayload>(
            json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

    private static WhatsAppWebhookPayload StatusPayload(WhatsAppMessageStatus status) => new()
    {
        Object = "whatsapp_business_account",
        Entry =
        [
            new WhatsAppEntry
            {
                Id = "acct-1",
                Changes =
                [
                    new WhatsAppChange
                    {
                        Field = "messages",
                        Value = new WhatsAppChangeValue
                        {
                            MessagingProduct = "whatsapp",
                            Metadata = new WhatsAppMetadata { PhoneNumberId = "phone-1", DisplayPhoneNumber = "+1" },
                            Statuses = [status],
                        },
                    },
                ],
            },
        ],
    };

    [Fact]
    public void AFailedStatus_BindsWithItsReason()
    {
        WhatsAppMessageStatus status = Bind(FailedStatusBody).Entry[0].Changes[0].Value.Statuses![0];

        WhatsAppStatusError error = Assert.Single(status.Errors!);
        Assert.Equal(131047, error.Code);
        Assert.Equal("Re-engagement message", error.Title);
        Assert.StartsWith("Message failed to send because more than 24 hours", error.ErrorData!.Details);
    }

    // Every field of an error is optional: a reason worded differently must not fail the batch.
    [Fact]
    public void AFailedStatus_WithABareError_StillBinds()
    {
        WhatsAppWebhookPayload payload = Bind(FailedStatusBody.Replace(
            "{\"code\":131047,\"title\":\"Re-engagement message\",\"message\":\"Re-engagement message\",", "{\"unexpected\":true,"));

        WhatsAppStatusError error = Assert.Single(payload.Entry[0].Changes[0].Value.Statuses![0].Errors!);
        Assert.Null(error.Code);
        Assert.Null(error.Title);
    }

    [Fact]
    public async Task ProcessEventAsync_FailedStatus_LogsWhyAsAWarning()
    {
        await _sut.ProcessEventAsync(Bind(FailedStatusBody), RawBody);

        _loggerMock.Verify(l => l.LogWarning(
            It.Is<string>(m => m.StartsWith("WhatsApp message failed.")),
            It.Is<object?[]>(a => (string)a[0]! == "wamid.F" && (int?)a[1] == 131047
                && (string?)a[2] == "Re-engagement message" && ((string?)a[3])!.Contains("24 hours"))), Times.Once);
        Assert.Empty(_published);
    }

    // Still a warning when Meta gives no reason, so the failure is not lost among info lines.
    [Fact]
    public async Task ProcessEventAsync_FailedStatusWithNoReason_StillWarns()
    {
        await _sut.ProcessEventAsync(StatusPayload(new WhatsAppMessageStatus { Id = "wamid.G", Status = "failed", Timestamp = "1700000000" }), RawBody);

        _loggerMock.Verify(l => l.LogWarning(
            It.Is<string>(m => m.StartsWith("WhatsApp message failed.")),
            It.Is<object?[]>(a => (string)a[0]! == "wamid.G" && a[1] == null && a[2] == null && a[3] == null)), Times.Once);
    }

    [Fact]
    public async Task ProcessEventAsync_DeliveredStatus_IsInformationOnly()
    {
        await _sut.ProcessEventAsync(StatusPayload(new WhatsAppMessageStatus { Id = "wamid.H", Status = "delivered", Timestamp = "1700000000" }), RawBody);

        _loggerMock.Verify(l => l.LogInformation(
            It.Is<string>(m => m.StartsWith("WhatsApp status update.")),
            It.Is<object?[]>(a => (string)a[0]! == "wamid.H" && (string)a[1]! == "delivered")), Times.Once);
        _loggerMock.Verify(l => l.LogWarning(It.IsAny<string>(), It.IsAny<object?[]>()), Times.Never);
    }

    // A 503 makes Meta send the webhook again, so a Kafka outage loses nothing.
    [Fact]
    public async Task ProcessEventAsync_WhenKafkaFails_ThrowsServiceUnavailable()
    {
        _busMock
            .Setup(b => b.PublishAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Local: Message timed out"));
        WhatsAppWebhookPayload payload = BuildPayload(
            "acct-1", "phone-1", "+1 555 000 9999", [("15550001111", "msg-001", "text", "Hi")]);

        await Assert.ThrowsAsync<ServiceUnavailableException>(() => _sut.ProcessEventAsync(payload, RawBody));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static WhatsAppConnection NewConnection(Guid id) => new()
    {
        Id = id,
        OwnerUserId = "owner-1",
        Name = "Support",
        VerifyTokenEncrypted = "encrypted",
        WebhookVerifiedAt = DateTime.UtcNow,
        WhatsAppBusinessAccountId = "business-account-1",
        PhoneNumberId = "phone-123",
    };

    private static WhatsAppWebhookPayload BuildPayload(
        string entryId,
        string phoneNumberId,
        string displayPhone,
        (string From, string Id, string Type, string? Body)[] messages)
    {
        return new WhatsAppWebhookPayload
        {
            Object = "whatsapp_business_account",
            Entry =
            [
                new WhatsAppEntry
                {
                    Id = entryId,
                    Changes = [BuildChange(phoneNumberId, displayPhone, messages)],
                },
            ],
        };
    }

    private static WhatsAppChange BuildChange(
        string phoneNumberId,
        string displayPhone,
        (string From, string Id, string Type, string? Body)[] messages)
    {
        return new WhatsAppChange
        {
            Field = "messages",
            Value = new WhatsAppChangeValue
            {
                MessagingProduct = "whatsapp",
                Metadata = new WhatsAppMetadata
                {
                    PhoneNumberId = phoneNumberId,
                    DisplayPhoneNumber = displayPhone,
                },
                Messages = messages.Select(m => new WhatsAppMessage
                {
                    From = m.From,
                    Id = m.Id,
                    Timestamp = "1700000000",
                    Type = m.Type,
                    Text = m.Body is not null ? new WhatsAppTextBody { Body = m.Body } : null,
                }).ToList(),
            },
        };
    }
}
