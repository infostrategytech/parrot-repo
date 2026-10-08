using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Moq;
using Parrot.Application.Integrations;
using Parrot.Application.Logging;
using Parrot.Application.Messaging;
using Parrot.Domain.Entities;

namespace Test.Parrot.Api.Tests.Services.Integrations;

// A manual connection's webhooks come from the business's own Meta app and are signed with its
// App Secret; a connection made through Parrot's app (Embedded Signup) is checked with the
// app-level one. The mode decides what a failed check does.
public class WebhookAuthenticationTests
{
    private const string Body = """{"object":"whatsapp_business_account","entry":[]}""";
    private const string AppSecret = "0123456789abcdef0123456789abcdef";
    private const string BusinessSecret = "fedcba9876543210fedcba9876543210";

    private readonly Mock<IAppLogger<WhatsAppWebhookService>> _whatsAppLogger = new();
    private readonly Mock<IAppLogger<FacebookWebhookService>> _facebookLogger = new();

    private static string Sign(string body, string secret) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    private sealed class Protector : IWhatsAppTokenProtector, IFacebookCredentialProtector
    {
        public string Protect(string value) => $"enc:{value}";

        public string Unprotect(string protectedValue) => protectedValue.StartsWith("enc:", StringComparison.Ordinal)
            ? protectedValue[4..]
            : throw new CryptographicException("The key ring has rotated.");
    }

    private WhatsAppWebhookService WhatsApp(WebhookSignatureMode mode, WhatsAppConnection? connection)
    {
        Mock<IWhatsAppConnectionRepository> connections = new();
        connections.Setup(c => c.GetActiveByIdentifierAsync("ref")).ReturnsAsync(connection);
        return new WhatsAppWebhookService(
            Options.Create(new WhatsAppSettings { AppSecret = AppSecret, SignatureMode = mode }),
            _whatsAppLogger.Object,
            new InboundMessagePublisher(Mock.Of<IMessageBus>(), Options.Create(new KafkaSettings()), Mock.Of<IAppLogger<InboundMessagePublisher>>()),
            connections.Object,
            new Protector());
    }

    private FacebookWebhookService Facebook(FacebookConnection? connection, WebhookSignatureMode? mode = null)
    {
        Mock<IFacebookConnectionRepository> connections = new();
        connections.Setup(c => c.GetActiveByIdentifierAsync("ref", It.IsAny<CancellationToken>())).ReturnsAsync(connection);
        FacebookSettings settings = new() { AppSecret = AppSecret };
        if (mode is { } chosen) settings.SignatureMode = chosen;
        return new FacebookWebhookService(
            Options.Create(settings),
            connections.Object,
            new InboundMessagePublisher(Mock.Of<IMessageBus>(), Options.Create(new KafkaSettings()), Mock.Of<IAppLogger<InboundMessagePublisher>>()),
            _facebookLogger.Object,
            new Protector());
    }

    private static WhatsAppConnection WhatsAppConnection(string? appSecretEncrypted) => new()
    {
        OwnerUserId = "owner",
        Name = "Support",
        VerifyTokenEncrypted = "enc:verify",
        AppSecretEncrypted = appSecretEncrypted,
    };

    private static FacebookConnection FacebookConnection(string? appSecretEncrypted) => new()
    {
        OwnerUserId = "owner",
        VerifyTokenEncrypted = "enc:verify",
        AppSecretEncrypted = appSecretEncrypted,
    };

    [Fact]
    public void ASignatureMatches_OnlyTheExactBodyUnderTheSameSecret()
    {
        string signature = Sign(Body, AppSecret);

        Assert.True(WebhookSignature.Matches(Body, signature, AppSecret));
        Assert.True(WebhookSignature.Matches(Body, signature.ToUpperInvariant().Replace("SHA256=", "sha256="), AppSecret));
        Assert.False(WebhookSignature.Matches(Body + " ", signature, AppSecret));
        Assert.False(WebhookSignature.Matches(Body, signature, BusinessSecret));
        Assert.False(WebhookSignature.Matches(Body, null, AppSecret));
        Assert.False(WebhookSignature.Matches(Body, "sha256=invalid", AppSecret));
        Assert.False(WebhookSignature.Matches(Body, signature[7..], AppSecret));
        Assert.False(WebhookSignature.Matches(Body, signature, ""));
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF", true)]
    [InlineData("0123456789abcdef0123456789abcde", false)]
    [InlineData("0123456789abcdef0123456789abcdeg", false)]
    [InlineData("185860297939218", false)]
    [InlineData("", false)]
    public void AnAppSecretIs32HexCharacters(string value, bool expected) =>
        Assert.Equal(expected, WebhookSignature.IsAppSecret(value));

    // Manual setup: the business's own app signs, so Parrot's secret must not be accepted for it.
    [Fact]
    public async Task AManualConnection_IsCheckedWithItsOwnSecret()
    {
        WhatsAppWebhookService service = WhatsApp(WebhookSignatureMode.Enforce, WhatsAppConnection($"enc:{BusinessSecret}"));

        Assert.True(await service.IsAuthenticAsync("ref", Body, Sign(Body, BusinessSecret)));
        Assert.False(await service.IsAuthenticAsync("ref", Body, Sign(Body, AppSecret)));
    }

    // Embedded Signup: no secret of its own, so Parrot's app-level secret applies.
    [Fact]
    public async Task AConnectionWithoutItsOwnSecret_IsCheckedWithTheAppSecret()
    {
        WhatsAppWebhookService service = WhatsApp(WebhookSignatureMode.Enforce, WhatsAppConnection(null));

        Assert.True(await service.IsAuthenticAsync("ref", Body, Sign(Body, AppSecret)));
        Assert.False(await service.IsAuthenticAsync("ref", Body, Sign(Body, BusinessSecret)));
    }

    [Fact]
    public async Task AWebhookWithNoReference_IsCheckedWithTheAppSecret()
    {
        WhatsAppWebhookService service = WhatsApp(WebhookSignatureMode.Enforce, connection: null);

        Assert.True(await service.IsAuthenticAsync(null, Body, Sign(Body, AppSecret)));
        Assert.False(await service.IsAuthenticAsync(null, Body, Sign(Body, BusinessSecret)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("sha256=0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task Enforce_RejectsAMissingOrWrongSignature(string? signature)
    {
        WhatsAppWebhookService service = WhatsApp(WebhookSignatureMode.Enforce, WhatsAppConnection($"enc:{BusinessSecret}"));

        Assert.False(await service.IsAuthenticAsync("ref", Body, signature));
        _whatsAppLogger.Verify(l => l.LogWarning(
            It.Is<string>(m => m.StartsWith("WhatsApp webhook rejected because its signature check failed.")),
            It.IsAny<object?[]>()), Times.Once);
    }

    // LogOnly keeps live connections working while they save their secret, but says so in the logs.
    [Fact]
    public async Task LogOnly_AcceptsAWrongSignature_AndWarns()
    {
        WhatsAppWebhookService service = WhatsApp(WebhookSignatureMode.LogOnly, WhatsAppConnection(null));

        Assert.True(await service.IsAuthenticAsync("ref", Body, Sign(Body, BusinessSecret)));
        _whatsAppLogger.Verify(l => l.LogWarning(
            It.Is<string>(m => m.Contains("accepted because SignatureMode is LogOnly")),
            It.Is<object?[]>(a => (string)a[0]! == "mismatch" && (string)a[1]! == "app")), Times.Once);
    }

    [Fact]
    public async Task Off_AcceptsWithoutCheckingOrLooking()
    {
        Mock<IWhatsAppConnectionRepository> connections = new();
        WhatsAppWebhookService service = new(
            Options.Create(new WhatsAppSettings { SignatureMode = WebhookSignatureMode.Off }),
            _whatsAppLogger.Object,
            new InboundMessagePublisher(Mock.Of<IMessageBus>(), Options.Create(new KafkaSettings()), Mock.Of<IAppLogger<InboundMessagePublisher>>()),
            connections.Object,
            new Protector());

        Assert.True(await service.IsAuthenticAsync("ref", Body, null));
        connections.VerifyNoOtherCalls();
    }

    // A rotated key ring cannot read the stored secret: that is a failure, never a pass.
    [Fact]
    public async Task AnUnreadableSecret_FailsTheCheck()
    {
        WhatsAppWebhookService service = WhatsApp(WebhookSignatureMode.Enforce, WhatsAppConnection("unreadable"));

        Assert.False(await service.IsAuthenticAsync("ref", Body, Sign(Body, AppSecret)));
        _whatsAppLogger.Verify(l => l.LogWarning(It.IsAny<string>(),
            It.Is<object?[]>(a => (string)a[0]! == "secret_unreadable" && (string)a[1]! == "connection")), Times.Once);
    }

    [Fact]
    public async Task NoSecretAnywhere_FailsTheCheck()
    {
        Mock<IWhatsAppConnectionRepository> connections = new();
        WhatsAppWebhookService service = new(
            Options.Create(new WhatsAppSettings { SignatureMode = WebhookSignatureMode.Enforce }),
            _whatsAppLogger.Object,
            new InboundMessagePublisher(Mock.Of<IMessageBus>(), Options.Create(new KafkaSettings()), Mock.Of<IAppLogger<InboundMessagePublisher>>()),
            connections.Object,
            new Protector());

        Assert.False(await service.IsAuthenticAsync(null, Body, Sign(Body, AppSecret)));
    }

    // WhatsApp starts in LogOnly so live businesses are not cut off; Facebook has always enforced.
    [Fact]
    public void TheDefaults_AreLogOnlyForWhatsApp_AndEnforceForFacebook()
    {
        Assert.Equal(WebhookSignatureMode.LogOnly, new WhatsAppSettings().SignatureMode);
        Assert.Equal(WebhookSignatureMode.Enforce, new FacebookSettings().SignatureMode);
    }

    [Fact]
    public async Task Facebook_AManualConnection_IsCheckedWithItsOwnSecret()
    {
        FacebookWebhookService service = Facebook(FacebookConnection($"enc:{BusinessSecret}"));

        Assert.True(await service.IsAuthenticAsync("ref", Body, Sign(Body, BusinessSecret)));
        Assert.False(await service.IsAuthenticAsync("ref", Body, Sign(Body, AppSecret)));
    }

    [Fact]
    public async Task Facebook_AConnectionWithoutItsOwnSecret_IsCheckedWithTheAppSecret()
    {
        FacebookWebhookService service = Facebook(FacebookConnection(null));

        Assert.True(await service.IsAuthenticAsync("ref", Body, Sign(Body, AppSecret)));
        Assert.False(await service.IsAuthenticAsync("ref", Body, null));
    }

    [Fact]
    public async Task Facebook_LogOnly_AcceptsAWrongSignature_AndWarns()
    {
        FacebookWebhookService service = Facebook(FacebookConnection(null), WebhookSignatureMode.LogOnly);

        Assert.True(await service.IsAuthenticAsync("ref", Body, Sign(Body, BusinessSecret)));
        _facebookLogger.Verify(l => l.LogWarning(
            It.Is<string>(m => m.Contains("accepted because SignatureMode is LogOnly")),
            It.IsAny<object?[]>()), Times.Once);
    }
}
