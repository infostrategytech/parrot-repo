using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Moq;
using Parrot.Api.Rest.Controllers;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Integrations;
using Parrot.Application.Logging;
using Parrot.Application.Messaging;
using Parrot.Domain.Entities;
using Parrot.Domain.Enums;
using Parrot.Infrastructure.SocialMediaIntegrations;

namespace Test.Parrot.Api.Tests.Services.Integrations;

public class TikTokWebhookTests
{
    private const string Secret = "app-secret";
    private const string Business = "biz-open-id";
    private const string Customer = "customer-open-id";
    private static readonly DateTimeOffset Now = new(2023, 11, 14, 22, 13, 20, TimeSpan.Zero);

    private readonly Mock<IMessageBus> _bus = new();
    private readonly List<(string Topic, string? Key, InboundMessage Message)> _published = [];
    private readonly TikTokCredentialProtector _protector = new(new EphemeralDataProtectionProvider());
    private readonly Mock<ITikTokConnectionRepository> _connections = new();
    private readonly Guid _connectionId = Guid.NewGuid();
    private TikTokConnection _connection;
    private readonly TikTokWebhookService _sut;

    public TikTokWebhookTests()
    {
        _bus.Setup(b => b.PublishAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Callback<string, string?, InboundMessage, CancellationToken>((topic, key, message, _) => _published.Add((topic, key, message)))
            .Returns(Task.CompletedTask);

        _connection = new TikTokConnection
        {
            Id = _connectionId, WorkspaceId = "owner-1", AppId = "7123456789",
            AppSecretEncrypted = _protector.Protect(Secret), TikTokOpenId = Business, DisplayName = "Parrot Test",
        };
        _connections.Setup(c => c.GetActiveByIdentifierAsync("reference", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _connection);

        _sut = new TikTokWebhookService(
            Options.Create(new TikTokSettings()), _connections.Object, _protector,
            new InboundMessagePublisher(_bus.Object, Options.Create(new KafkaSettings()), Mock.Of<IAppLogger<InboundMessagePublisher>>()),
            Mock.Of<IAppLogger<TikTokWebhookService>>(), new FixedTime(Now));
    }

    // ---- the signature -------------------------------------------------------

    [Fact]
    public void ASignatureMadeWithTheSecret_IsAccepted_AsTikTokWritesIt()
    {
        string body = Event("im_receive_msg", Message("m1"));

        Assert.Null(TikTokWebhookSignature.Check(body, Sign(body, Secret, Now), Secret, Now, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void TheHeaderMayListItsPartsInAnyOrder_WithSpaces()
    {
        string body = Event("im_receive_msg", Message("m1"));
        string[] parts = Sign(body, Secret, Now).Split(',');

        Assert.Null(TikTokWebhookSignature.Check(body, $"{parts[1]} , {parts[0]}", Secret, Now, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void ABodyOrSecretOrTimestampThatWasChanged_Mismatches()
    {
        string body = Event("im_receive_msg", Message("m1"));
        string header = Sign(body, Secret, Now);

        Assert.Equal("mismatch", TikTokWebhookSignature.Check(body + " ", header, Secret, Now, TimeSpan.FromMinutes(5)));
        Assert.Equal("mismatch", TikTokWebhookSignature.Check(body, header, "other-secret", Now, TimeSpan.FromMinutes(5)));
        // The timestamp is signed: moving it to look fresh breaks the signature.
        string replayed = Sign(body, Secret, Now.AddHours(-2));
        string forged = $"t={Now.ToUnixTimeSeconds()},{replayed.Split(',')[1]}";
        Assert.Equal("mismatch", TikTokWebhookSignature.Check(body, forged, Secret, Now, TimeSpan.FromMinutes(5)));
    }

    [Theory]
    [InlineData(-299, true)]
    [InlineData(299, true)]
    [InlineData(-301, false)]
    [InlineData(301, false)]
    public void OnlyATimestampWithinTheToleranceIsAccepted(int secondsFromNow, bool accepted)
    {
        string body = Event("im_receive_msg", Message("m1"));

        string? reason = TikTokWebhookSignature.Check(body, Sign(body, Secret, Now.AddSeconds(secondsFromNow)), Secret, Now, TimeSpan.FromMinutes(5));

        Assert.Equal(accepted ? null : "stale_timestamp", reason);
    }

    [Theory]
    [InlineData(null, "missing_signature")]
    [InlineData("", "missing_signature")]
    [InlineData("nonsense", "malformed_signature")]
    [InlineData("t=abc,s=0000000000000000000000000000000000000000000000000000000000000000", "malformed_signature")]
    [InlineData("t=1700000000,s=not-hex", "malformed_signature")]
    [InlineData("t=1700000000", "malformed_signature")]
    public void AMissingOrUnreadableHeader_IsRefusedWithAReason(string? header, string reason)
    {
        Assert.Equal(reason, TikTokWebhookSignature.Check("{}", header, Secret, Now, TimeSpan.FromMinutes(5)));
    }

    // ---- the service ---------------------------------------------------------

    [Fact]
    public async Task AServiceCheck_UsesTheConnectionsOwnDecryptedSecret()
    {
        string body = Event("im_receive_msg", Message("m1"));

        Assert.True(await _sut.IsAuthenticAsync("reference", body, Sign(body, Secret, Now)));
        Assert.False(await _sut.IsAuthenticAsync("reference", body, Sign(body, "another-customers-secret", Now)));
        Assert.False(await _sut.IsAuthenticAsync("reference", body, null));
        Assert.False(await _sut.IsAuthenticAsync("unknown", body, Sign(body, Secret, Now)));
        Assert.False(await _sut.IsAuthenticAsync("", body, Sign(body, Secret, Now)));
    }

    [Fact]
    public async Task ASecretThatCannotBeDecrypted_IsRefusedNotThrown()
    {
        _connection.AppSecretEncrypted = "not-protected-by-this-key-ring";
        string body = Event("im_receive_msg", Message("m1"));

        Assert.False(await _sut.IsAuthenticAsync("reference", body, Sign(body, Secret, Now)));
    }

    [Fact]
    public async Task ACustomersMessage_IsPublishedKeyedByConversation_WithTheBodyUntouched()
    {
        string body = Event("im_receive_msg", Message("m1", text: "Hello"));

        Assert.True(await _sut.ProcessEventAsync("reference", body));

        (string topic, string? key, InboundMessage message) = Assert.Single(_published);
        Assert.Equal("messages.inbound", topic);
        Assert.Equal($"tiktok:{Business}:{Customer}", key);
        Assert.Equal("tiktok:m1", message.MessageId);
        Assert.Equal(MessagingPlatform.TikTok, message.Source);
        Assert.Equal(Customer, message.Sender);
        Assert.Equal(Business, message.Receiver);
        Assert.Equal(body, message.Data);
        Assert.Equal(new DateTime(2023, 11, 14, 22, 13, 20, 123, DateTimeKind.Utc), message.SentAt);
        Assert.Equal(Now.UtcDateTime, message.ReceivedAt);
        Assert.Equal(_connectionId, message.ConnectionId);
        Assert.Equal("owner-1", message.OwnerUserId);
    }

    // im_send_msg is the business's own message coming back; storing it would show it as the customer's.
    [Theory]
    [InlineData("im_send_msg")]
    [InlineData("im_mark_read_msg")]
    [InlineData("something_new")]
    public async Task EventsThatAreNotACustomersMessage_AreAnsweredButNotPublished(string name)
    {
        Assert.True(await _sut.ProcessEventAsync("reference", Event(name, Message("m1"))));

        Assert.Empty(_published);
    }

    [Fact]
    public async Task AnEventForAnotherAccount_IsRefused()
    {
        Assert.False(await _sut.ProcessEventAsync("reference", Event("im_receive_msg", Message("m1"), business: "someone-else")));

        Assert.Empty(_published);
    }

    [Fact]
    public async Task UnknownDisabledOrNotYetAuthorizedConnections_AreRefused()
    {
        string body = Event("im_receive_msg", Message("m1"));

        Assert.False(await _sut.ProcessEventAsync("unknown", body));
        Assert.False(await _sut.ProcessEventAsync(" ", body));
        _connection.TikTokOpenId = null;
        Assert.False(await _sut.ProcessEventAsync("reference", body));
        Assert.Empty(_published);
    }

    // A resend of a body that can never be read would fail the same way; it is answered so TikTok stops.
    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"user_openid\":\"biz-open-id\"}")]
    public async Task ABodyThatIsNotAnEvent_IsAnsweredNotRetried(string body)
    {
        Assert.True(await _sut.ProcessEventAsync("reference", body));

        Assert.Empty(_published);
    }

    [Fact]
    public async Task AMessageWithoutAnIdOrASender_IsAnsweredNotPublished()
    {
        string noId = Event("im_receive_msg", new { conversation_id = "c1", from_user = new { id = Customer } });
        string noSender = Event("im_receive_msg", new { message_id = "m1", conversation_id = "c1" });

        Assert.True(await _sut.ProcessEventAsync("reference", noId));
        Assert.True(await _sut.ProcessEventAsync("reference", noSender));
        Assert.Empty(_published);
    }

    [Fact]
    public async Task WhenKafkaIsDown_TheWebhookFailsSoTikTokResends()
    {
        _bus.Setup(b => b.PublishAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("broker down"));

        await Assert.ThrowsAsync<global::Parrot.Domain.Exceptions.ServiceUnavailableException>(
            () => _sut.ProcessEventAsync("reference", Event("im_receive_msg", Message("m1"))));
    }

    // ---- the controller ------------------------------------------------------

    [Fact]
    public async Task TheControllerChecksTheSignatureFirst_ThenRoutes()
    {
        Mock<ITikTokWebhookService> service = new();
        service.Setup(s => s.IsAuthenticAsync("reference", "body", "good", default)).ReturnsAsync(true);
        service.Setup(s => s.ProcessEventAsync("reference", "body", default)).ReturnsAsync(true);
        service.Setup(s => s.ProcessEventAsync("elsewhere", "body", default)).ReturnsAsync(false);
        service.Setup(s => s.IsAuthenticAsync("elsewhere", "body", "good", default)).ReturnsAsync(true);

        Assert.IsType<OkResult>(await Controller(service.Object, "body").HandleEvent("reference", "good"));
        Assert.IsType<UnauthorizedResult>(await Controller(service.Object, "body").HandleEvent("reference", "bad"));
        Assert.Equal(StatusCodes.Status403Forbidden,
            Assert.IsType<StatusCodeResult>(await Controller(service.Object, "body").HandleEvent("elsewhere", "good")).StatusCode);

        // An unsigned request is never routed.
        service.Verify(s => s.ProcessEventAsync("reference", "body", default), Times.Once);
    }

    // ---- helpers -------------------------------------------------------------

    private static TikTokWebhookController Controller(ITikTokWebhookService service, string rawBody)
    {
        TikTokWebhookController controller = new(service)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        controller.HttpContext.Items["RawBody"] = rawBody;
        return controller;
    }

    private static object Message(string id, string text = "Hello") => new
    {
        message_id = id,
        conversation_id = "conversation-1",
        timestamp = 1700000000123,
        type = "text",
        from = "Ada",
        from_user = new { id = Customer },
        to = "parrot_test",
        to_user = new { id = Business },
        text = new { body = text },
    };

    // TikTok sends "content" as JSON inside a JSON string.
    public static string Event(string name, object content, string business = Business) => JsonSerializer.Serialize(new
    {
        @event = name, user_openid = business, client_key = "7123456789", create_time = 1700000000,
        content = JsonSerializer.Serialize(content),
    });

    // As TikTok signs: HMAC-SHA256 of "{t}.{raw body}" with the App Secret, in hex.
    public static string Sign(string body, string secret, DateTimeOffset at)
    {
        long t = at.ToUnixTimeSeconds();
        byte[] mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.{body}"));
        return $"t={t},s={Convert.ToHexString(mac).ToLowerInvariant()}";
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
