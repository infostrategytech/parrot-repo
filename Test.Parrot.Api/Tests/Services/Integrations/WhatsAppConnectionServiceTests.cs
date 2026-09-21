using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Parrot.Api.Rest.Controllers;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Integrations;
using Parrot.Domain.Entities;
using Parrot.Domain.Exceptions;
using Parrot.Infrastructure.SocialMediaIntegrations;

namespace Test.Parrot.Api.Tests.Services.Integrations;

public class WhatsAppConnectionServiceTests
{
    private readonly Mock<IWhatsAppConnectionRepository> _repository = new();
    private readonly IWhatsAppTokenProtector _protector = new WhatsAppTokenProtector(new EphemeralDataProtectionProvider());

    public WhatsAppConnectionServiceTests()
    {
        _repository.Setup(r => r.GetByOwnerAsync(It.IsAny<string>()))
            .ReturnsAsync(Array.Empty<WhatsAppConnection>());
    }

    [Fact]
    public async Task SaveAccessToken_EncryptsAndCanRotateAfterReceivingMessages()
    {
        var connection = Connection();
        connection.FirstMessageReceivedAt = DateTime.UtcNow;
        _repository.Setup(r => r.GetByOwnerAndIdAsync("owner", connection.Id)).ReturnsAsync(connection);
        string? stored = null;
        _repository.Setup(r => r.SaveAccessTokenAsync(connection.Id, "owner", It.IsAny<string>()))
            .Callback<Guid, string, string>((id, owner, encrypted) => stored = encrypted)
            .Returns(Task.CompletedTask);
        await CreateService().SaveAccessTokenAsync("owner", connection.Id, new() { AccessToken = "test-token" });
        Assert.NotEqual("test-token", stored);
        Assert.Equal("test-token", _protector.Unprotect(stored!));
        connection.AccessTokenEncrypted = stored;
        var response = await CreateService().GetByIdAsync("owner", connection.Id);
        Assert.True(response.HasAccessToken);
        string json = System.Text.Json.JsonSerializer.Serialize(response);
        Assert.DoesNotContain("test-token", json);
        Assert.DoesNotContain(stored!, json);
        await CreateService().SaveAccessTokenAsync("owner", connection.Id, new() { AccessToken = "replacement-token" });
        Assert.Equal("replacement-token", _protector.Unprotect(stored!));
    }

    [Fact]
    public async Task SaveAccessToken_RejectsOtherOwnerAndDisabledConnection()
    {
        var connection = Connection();
        var service = CreateService();
        await Assert.ThrowsAsync<NotFoundException>(() => service.SaveAccessTokenAsync("other", connection.Id, new() { AccessToken = "test" }));
        connection.DisabledAt = DateTime.UtcNow;
        _repository.Setup(r => r.GetByOwnerAndIdAsync("owner", connection.Id)).ReturnsAsync(connection);
        await Assert.ThrowsAsync<ConflictException>(() => service.SaveAccessTokenAsync("owner", connection.Id, new() { AccessToken = "test" }));
        _repository.Verify(r => r.SaveAccessTokenAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("bad token")]
    [InlineData("bad\r\ntoken")]
    public async Task SaveAccessToken_RejectsInvalidToken(string token)
    {
        var connection = Connection();
        _repository.Setup(r => r.GetByOwnerAndIdAsync("owner", connection.Id)).ReturnsAsync(connection);
        await Assert.ThrowsAsync<ValidationException>(() => CreateService().SaveAccessTokenAsync("owner", connection.Id, new() { AccessToken = token }));
        _repository.Verify(r => r.SaveAccessTokenAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Create_OmittedNameDefaultsToWhatsAppBusiness()
    {
        CreateWhatsAppConnectionRequest request = System.Text.Json.JsonSerializer
            .Deserialize<CreateWhatsAppConnectionRequest>("{}")!;

        WhatsAppConnectionSetupResponse result = await CreateService().CreateAsync("owner", request);

        Assert.Equal("WhatsApp Business", result.Connection.Name);
        Assert.Matches("^[a-f0-9]{64}$", result.VerifyToken);
    }

    [Fact]
    public async Task Create_ResumesExistingConnectionWithoutEditingCredentialsOrName()
    {
        WhatsAppConnection connection = Connection();
        _repository.Setup(r => r.GetByOwnerAsync("owner")).ReturnsAsync(new[] { connection });

        var result = await CreateService().CreateAsync("owner", new() { Name = "Changed name" });

        Assert.Equal(connection.Id, result.Connection.Id);
        Assert.Equal(connection.Name, result.Connection.Name);
        Assert.Equal(_protector.Unprotect(connection.VerifyTokenEncrypted), result.VerifyToken);
        _repository.Verify(r => r.AddAsync(It.IsAny<WhatsAppConnection>()), Times.Never);
    }

    [Fact]
    public async Task Create_ResumesWinnerWhenConcurrentRequestCreatesConnection()
    {
        WhatsAppConnection connection = Connection();
        _repository.SetupSequence(r => r.GetByOwnerAsync("owner"))
            .ReturnsAsync(Array.Empty<WhatsAppConnection>()).ReturnsAsync(new[] { connection });
        _repository.Setup(r => r.AddAsync(It.IsAny<WhatsAppConnection>()))
            .ThrowsAsync(new ConflictException("Already exists"));

        var result = await CreateService().CreateAsync("owner", new() { Name = "WhatsApp Business" });

        Assert.Equal(connection.Id, result.Connection.Id);
        Assert.Equal(_protector.Unprotect(connection.VerifyTokenEncrypted), result.VerifyToken);
    }

    [Fact]
    public async Task Create_DoesNotReplaceDisabledConnection()
    {
        WhatsAppConnection connection = Connection();
        connection.DisabledAt = DateTime.UtcNow;
        _repository.Setup(r => r.GetByOwnerAsync("owner")).ReturnsAsync(new[] { connection });

        await Assert.ThrowsAsync<ConflictException>(() =>
            CreateService().CreateAsync("owner", new() { Name = "WhatsApp Business" }));
        _repository.Verify(r => r.AddAsync(It.IsAny<WhatsAppConnection>()), Times.Never);
    }

    private WhatsAppConnectionService CreateService(string baseUrl = "https://api.example.com/") =>
        new(_repository.Object, _protector, Options.Create(new WhatsAppSettings { WebhookBaseUrl = baseUrl }));

    [Fact]
    public async Task Create_GeneratesIndependentCredentialsAndStoresOnlyEncryptedToken()
    {
        WhatsAppConnection? saved = null;
        _repository.Setup(r => r.AddAsync(It.IsAny<WhatsAppConnection>()))
            .Callback<WhatsAppConnection>(c => saved = c).Returns(Task.CompletedTask);

        WhatsAppConnectionSetupResponse result = await CreateService().CreateAsync("owner", new() { Name = " Support " });

        Assert.NotNull(saved);
        Assert.Equal("owner", saved.OwnerUserId);
        Assert.Equal("Support", saved.Name);
        Assert.Matches("^[a-f0-9]{64}$", saved.WebhookIdentifier);
        Assert.Matches("^[a-f0-9]{64}$", result.VerifyToken);
        Assert.NotEqual(saved.WebhookIdentifier, result.VerifyToken);
        Assert.NotEqual(result.VerifyToken, saved.VerifyTokenEncrypted);
        Assert.Equal(result.VerifyToken, _protector.Unprotect(saved.VerifyTokenEncrypted));
        Assert.Equal($"https://api.example.com/api/webhooks/whatsapp?ref={saved.WebhookIdentifier}", result.WebhookUrl);
        Assert.Equal("awaiting-webhook-verification", result.Connection.SetupStatus);
    }

    [Fact]
    public async Task Setup_ReusesCredentialsAndUsesCurrentEnvironmentBaseUrl()
    {
        WhatsAppConnection connection = Connection();
        _repository.Setup(r => r.GetByOwnerAndIdAsync("owner", connection.Id)).ReturnsAsync(connection);

        WhatsAppConnectionSetupResponse first = await CreateService().GetSetupAsync("owner", connection.Id);
        WhatsAppConnectionSetupResponse second = await CreateService("https://new.example.com").GetSetupAsync("owner", connection.Id);

        Assert.Equal(first.VerifyToken, second.VerifyToken);
        Assert.Equal($"https://new.example.com/api/webhooks/whatsapp?ref={connection.WebhookIdentifier}", second.WebhookUrl);
        _repository.Verify(r => r.AddAsync(It.IsAny<WhatsAppConnection>()), Times.Never);
    }

    [Fact]
    public async Task AnotherOwner_CannotReadSetupOrDisable()
    {
        Guid id = Guid.NewGuid();
        await Assert.ThrowsAsync<NotFoundException>(() => CreateService().GetSetupAsync("another-owner", id));
        await Assert.ThrowsAsync<NotFoundException>(() => CreateService().DisableAsync("another-owner", id));
        _repository.Verify(r => r.DisableAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>()), Times.Never);
    }

    [Fact]
    public async Task DisabledConnection_DoesNotExposeSetupCredentials()
    {
        WhatsAppConnection connection = Connection();
        connection.DisabledAt = DateTime.UtcNow;
        _repository.Setup(r => r.GetByOwnerAndIdAsync("owner", connection.Id)).ReturnsAsync(connection);
        await Assert.ThrowsAsync<ConflictException>(() => CreateService().GetSetupAsync("owner", connection.Id));
    }

    [Theory]
    [InlineData("subscribe", "wrong-token")]
    [InlineData("unsubscribe", "correct-token")]
    [InlineData("subscribe", "")]
    public async Task InvalidChallenge_DoesNotMarkConnectionVerified(string mode, string token)
    {
        WhatsAppConnection connection = Connection();
        _repository.Setup(r => r.GetActiveByIdentifierAsync(connection.WebhookIdentifier)).ReturnsAsync(connection);
        Assert.False(await CreateService().VerifyWebhookAsync(connection.WebhookIdentifier, mode, token));
        _repository.Verify(r => r.MarkWebhookVerifiedAsync(It.IsAny<Guid>(), It.IsAny<DateTime>()), Times.Never);
    }

    [Fact]
    public async Task ValidChallenge_MarksConnectionVerified()
    {
        WhatsAppConnection connection = Connection();
        _repository.Setup(r => r.GetActiveByIdentifierAsync(connection.WebhookIdentifier)).ReturnsAsync(connection);
        _repository.Setup(r => r.MarkWebhookVerifiedAsync(connection.Id, It.IsAny<DateTime>())).ReturnsAsync(true);
        Assert.True(await CreateService().VerifyWebhookAsync(connection.WebhookIdentifier, "subscribe", "correct-token"));
        _repository.Verify(r => r.MarkWebhookVerifiedAsync(connection.Id, It.Is<DateTime>(t => t.Kind == DateTimeKind.Utc)), Times.Once);
    }

    [Fact]
    public async Task ConnectionDisabledDuringVerification_IsRejected()
    {
        WhatsAppConnection connection = Connection();
        _repository.Setup(r => r.GetActiveByIdentifierAsync(connection.WebhookIdentifier)).ReturnsAsync(connection);
        _repository.Setup(r => r.MarkWebhookVerifiedAsync(connection.Id, It.IsAny<DateTime>())).ReturnsAsync(false);
        Assert.False(await CreateService().VerifyWebhookAsync(connection.WebhookIdentifier, "subscribe", "correct-token"));
    }

    [Fact]
    public async Task UnknownOrDisabledIdentifier_IsRejected()
    {
        Assert.False(await CreateService().VerifyWebhookAsync("unknown", "subscribe", "correct-token"));
        _repository.Verify(r => r.MarkWebhookVerifiedAsync(It.IsAny<Guid>(), It.IsAny<DateTime>()), Times.Never);
    }

    [Fact]
    public async Task UnreadableCiphertext_IsRejected()
    {
        WhatsAppConnection connection = Connection();
        connection.VerifyTokenEncrypted = "corrupt-ciphertext";
        _repository.Setup(r => r.GetActiveByIdentifierAsync(connection.WebhookIdentifier)).ReturnsAsync(connection);
        Assert.False(await CreateService().VerifyWebhookAsync(connection.WebhookIdentifier, "subscribe", "correct-token"));
    }

    [Fact]
    public async Task Callback_EchoesChallengeAsPlainTextAndRejectsInvalidToken()
    {
        Mock<IWhatsAppConnectionService> connections = new();
        connections.Setup(s => s.VerifyWebhookAsync("identifier", "subscribe", "correct")).ReturnsAsync(true);
        WhatsAppWebhookController controller = new(Mock.Of<IWhatsAppWebhookService>());
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        controller.Request.QueryString = new QueryString("?ref=identifier");
        ContentResult success = Assert.IsType<ContentResult>(await controller.VerifyConnectionWebhook(
            "identifier", "subscribe", "correct", "12345", connections.Object));
        Assert.Equal("12345", success.Content);
        Assert.Equal("text/plain", success.ContentType);
        StatusCodeResult rejected = Assert.IsType<StatusCodeResult>(await controller.VerifyConnectionWebhook(
            "identifier", "subscribe", "wrong", "12345", connections.Object));
        Assert.Equal(403, rejected.StatusCode);
    }

    [Fact]
    public async Task EmptyReference_DoesNotFallBackToSharedVerification()
    {
        Mock<IWhatsAppWebhookService> legacy = new();
        WhatsAppWebhookController controller = new(legacy.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        controller.Request.QueryString = new QueryString("?ref=");
        StatusCodeResult result = Assert.IsType<StatusCodeResult>(await controller.VerifyConnectionWebhook(
            null, "subscribe", "token", "challenge", Mock.Of<IWhatsAppConnectionService>()));
        Assert.Equal(403, result.StatusCode);
        legacy.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("sha256=invalid")]
    public async Task IncomingReference_WithMissingOrInvalidSignature_IsProcessedWhenValidationIsBypassed(string? signature)
    {
        WhatsAppWebhookPayload payload = new()
        {
            Object = "whatsapp_business_account",
            Entry = [],
        };
        Mock<IWhatsAppWebhookService> webhooks = new();
        webhooks.Setup(s => s.ProcessConnectionEventAsync("identifier", payload, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        WhatsAppWebhookController controller = new(webhooks.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        controller.Request.QueryString = new QueryString("?ref=identifier");
        controller.HttpContext.Items["RawBody"] = "raw-body";

        OkResult result = Assert.IsType<OkResult>(await controller.HandleEvent(signature, "identifier", payload));

        Assert.Equal(200, result.StatusCode);
        webhooks.Verify(s => s.ProcessConnectionEventAsync("identifier", payload, It.IsAny<CancellationToken>()), Times.Once);
        webhooks.Verify(s => s.IsValidSignature(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        webhooks.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task IncomingReference_WithValidSignature_IsProcessed()
    {
        WhatsAppWebhookPayload payload = new()
        {
            Object = "whatsapp_business_account",
            Entry = [],
        };
        Mock<IWhatsAppWebhookService> webhooks = new();
        webhooks.Setup(s => s.IsValidSignature("raw-body", "sha256=valid")).Returns(true);
        webhooks.Setup(s => s.ProcessConnectionEventAsync("identifier", payload, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        WhatsAppWebhookController controller = new(webhooks.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        controller.HttpContext.Items["RawBody"] = "raw-body";
        controller.Request.QueryString = new QueryString("?ref=identifier");

        Assert.IsType<OkResult>(await controller.HandleEvent("sha256=valid", "identifier", payload));
        webhooks.Verify(s => s.ProcessConnectionEventAsync(
            "identifier", payload, controller.HttpContext.RequestAborted), Times.Once);
        webhooks.Verify(s => s.ProcessEventAsync(
            It.IsAny<WhatsAppWebhookPayload>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task IncomingReference_WithUnknownConnection_IsRejected()
    {
        WhatsAppWebhookPayload payload = new()
        {
            Object = "whatsapp_business_account",
            Entry = [],
        };
        Mock<IWhatsAppWebhookService> webhooks = new();
        webhooks.Setup(s => s.IsValidSignature("raw-body", "sha256=valid")).Returns(true);
        webhooks.Setup(s => s.ProcessConnectionEventAsync("unknown", payload, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        WhatsAppWebhookController controller = new(webhooks.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        controller.HttpContext.Items["RawBody"] = "raw-body";
        controller.Request.QueryString = new QueryString("?ref=unknown");

        StatusCodeResult result = Assert.IsType<StatusCodeResult>(
            await controller.HandleEvent("sha256=valid", "unknown", payload));
        Assert.Equal(403, result.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task InvalidName_IsRejectedBeforePersistence(string name)
    {
        await Assert.ThrowsAsync<ValidationException>(() => CreateService().CreateAsync("owner", new() { Name = name }));
        _repository.Verify(r => r.AddAsync(It.IsAny<WhatsAppConnection>()), Times.Never);
    }

    [Fact]
    public void Production_RequiresDurableKeyConfiguration()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddWhatsAppCredentialProtection(configuration, false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TokenProtection_SurvivesProviderRestartWithSameKeyRing(bool protectKeysWithCertificate)
    {
        string path = Path.Combine(Path.GetTempPath(), "parrot-key-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(path);
            Dictionary<string, string?> settings = new()
            {
                ["DataProtection:KeyRingPath"] = Path.Combine(path, "keys"),
            };
            if (protectKeysWithCertificate)
            {
                using RSA rsa = RSA.Create(2048);
                CertificateRequest request = new("CN=Parrot test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
                string certificatePath = Path.Combine(path, "test.pfx");
                File.WriteAllBytes(certificatePath, certificate.Export(X509ContentType.Pfx, "test-password"));
                settings["DataProtection:CertificatePath"] = certificatePath;
                settings["DataProtection:CertificatePassword"] = "test-password";
            }

            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            string encrypted;
            using (ServiceProvider first = new ServiceCollection().AddWhatsAppCredentialProtection(configuration, !protectKeysWithCertificate).BuildServiceProvider())
            {
                encrypted = first.GetRequiredService<IWhatsAppTokenProtector>().Protect("persistent-token");
                Assert.NotEqual("persistent-token", encrypted);
            }

            using ServiceProvider restarted = new ServiceCollection().AddWhatsAppCredentialProtection(configuration, !protectKeysWithCertificate).BuildServiceProvider();
            Assert.Equal("persistent-token", restarted.GetRequiredService<IWhatsAppTokenProtector>().Unprotect(encrypted));
            Assert.Throws<CryptographicException>(() => new WhatsAppTokenProtector(new EphemeralDataProtectionProvider()).Unprotect(encrypted));
            if (protectKeysWithCertificate)
            {
                string keyFile = Assert.Single(Directory.GetFiles(Path.Combine(path, "keys"), "key-*.xml"));
                Assert.Contains("encryptedSecret", File.ReadAllText(keyFile));
            }
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }

    private WhatsAppConnection Connection() => new()
    {
        OwnerUserId = "owner",
        Name = "Support",
        VerifyTokenEncrypted = _protector.Protect("correct-token"),
    };
}
