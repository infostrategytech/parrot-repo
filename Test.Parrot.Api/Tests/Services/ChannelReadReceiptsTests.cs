using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Moq;
using Parrot.Application.Conversations;
using Parrot.Application.Integrations;
using Parrot.Application.Logging;
using Parrot.Domain.Entities;
using Parrot.Domain.Enums;
using Parrot.Infrastructure.Conversations;

namespace Test.Parrot.Api.Tests.Services;

public class ChannelReadReceiptsTests
{
    private const string Owner = "owner";
    private const string Customer = "2348012345678";

    private readonly Mock<IWhatsAppConnectionRepository> _whatsAppConnections = new();
    private readonly Mock<IWhatsAppSender> _whatsApp = new();
    private readonly Mock<IWhatsAppTokenProtector> _whatsAppProtector = new();
    private readonly Mock<IFacebookConnectionRepository> _facebookConnections = new();
    private readonly Mock<IFacebookSender> _facebook = new();
    private readonly Mock<IFacebookCredentialProtector> _facebookProtector = new();
    private readonly Mock<IInstagramConnectionRepository> _instagramConnections = new();
    private readonly Mock<IInstagramSender> _instagram = new();
    private readonly Mock<IInstagramCredentialProtector> _instagramProtector = new();
    private readonly InboxSettings _settings = new();

    private readonly WhatsAppConnection _whatsAppConnection = new()
    {
        WorkspaceId = Owner, Name = "Shop", VerifyTokenEncrypted = "x", PhoneNumberId = "phone-1", AccessTokenEncrypted = "enc-wa",
    };
    private readonly FacebookConnection _page = new()
    {
        WorkspaceId = Owner, VerifyTokenEncrypted = "x", PageId = "page-1", AccessTokenEncrypted = "enc-fb",
    };
    private readonly InstagramConnection _account = new()
    {
        WorkspaceId = Owner, VerifyTokenEncrypted = "x", AccountId = "ig-1", AccessTokenEncrypted = "enc-ig",
    };

    public ChannelReadReceiptsTests()
    {
        _whatsAppConnections.Setup(c => c.GetByOwnerAndIdAsync(Owner, _whatsAppConnection.Id)).ReturnsAsync(_whatsAppConnection);
        _facebookConnections.Setup(c => c.GetByOwnerAsync(Owner, It.IsAny<CancellationToken>())).ReturnsAsync(_page);
        _instagramConnections.Setup(c => c.GetByOwnerAsync(Owner, It.IsAny<CancellationToken>())).ReturnsAsync(_account);
        _whatsAppProtector.Setup(p => p.Unprotect("enc-wa")).Returns("wa-token");
        _facebookProtector.Setup(p => p.Unprotect("enc-fb")).Returns("fb-token");
        _instagramProtector.Setup(p => p.Unprotect("enc-ig")).Returns("ig-token");
    }

    private ChannelReadReceipts Receipts() => new(
        _whatsAppConnections.Object, _whatsApp.Object, _whatsAppProtector.Object,
        _facebookConnections.Object, _facebook.Object, _facebookProtector.Object,
        _instagramConnections.Object, _instagram.Object, _instagramProtector.Object,
        Options.Create(_settings), Mock.Of<IAppLogger<ChannelReadReceipts>>());

    [Fact]
    public async Task WhatsAppIsToldTheCustomersNewestMessageWasRead_ByItsWamid()
    {
        await Receipts().SendAsync(Owner, new ReadAdvance(_whatsAppConnection.Id, Customer, MessagingPlatform.WhatsApp, "whatsapp:wamid.IN1"), default);

        _whatsApp.Verify(s => s.MarkReadAsync("phone-1", "wa-token", "wamid.IN1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MessengerShowsTheCustomerTheirMessageWasSeen()
    {
        await Receipts().SendAsync(Owner, new ReadAdvance(_page.Id, "psid-1", MessagingPlatform.Facebook, "facebook:m_1"), default);

        _facebook.Verify(s => s.MarkSeenAsync("page-1", "fb-token", "psid-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InstagramShowsTheCustomerTheirMessageWasSeen()
    {
        await Receipts().SendAsync(Owner, new ReadAdvance(_account.Id, "igsid-1", MessagingPlatform.Instagram, "instagram:m_1"), default);

        _instagram.Verify(s => s.MarkSeenAsync("ig-1", "ig-token", "igsid-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TikTokHasNoReadCallYet()
    {
        await Receipts().SendAsync(Owner, new ReadAdvance(Guid.NewGuid(), "open-id", MessagingPlatform.TikTok, "tiktok:1"), default);

        _whatsApp.VerifyNoOtherCalls();
        _facebook.VerifyNoOtherCalls();
        _instagram.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WithTheSwitchOffNothingIsSent()
    {
        _settings.SendReadReceipts = false;

        await Receipts().SendAsync(Owner, new ReadAdvance(_whatsAppConnection.Id, Customer, MessagingPlatform.WhatsApp, "whatsapp:wamid.IN1"), default);

        _whatsApp.VerifyNoOtherCalls();
        _whatsAppConnections.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ADisabledConnectionOrOneWithoutATokenIsLeftAlone()
    {
        _whatsAppConnection.DisabledAt = DateTime.UtcNow;
        _page.AccessTokenEncrypted = null;
        _account.DisabledAt = DateTime.UtcNow;

        await Receipts().SendAsync(Owner, new ReadAdvance(_whatsAppConnection.Id, Customer, MessagingPlatform.WhatsApp, "whatsapp:wamid.IN1"), default);
        await Receipts().SendAsync(Owner, new ReadAdvance(_page.Id, "psid-1", MessagingPlatform.Facebook, "facebook:m_1"), default);
        await Receipts().SendAsync(Owner, new ReadAdvance(_account.Id, "igsid-1", MessagingPlatform.Instagram, "instagram:m_1"), default);

        _whatsApp.VerifyNoOtherCalls();
        _facebook.VerifyNoOtherCalls();
        _instagram.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AReadOnAConnectionThatIsNotThisOwnersIsNotSent()
    {
        await Receipts().SendAsync(Owner, new ReadAdvance(Guid.NewGuid(), Customer, MessagingPlatform.WhatsApp, "whatsapp:wamid.IN1"), default);
        await Receipts().SendAsync(Owner, new ReadAdvance(Guid.NewGuid(), "psid-1", MessagingPlatform.Facebook, "facebook:m_1"), default);

        _whatsApp.VerifyNoOtherCalls();
        _facebook.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AChannelThatRefusesOrCannotBeReachedNeverFailsTheRead()
    {
        _whatsApp.Setup(s => s.MarkReadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("WhatsApp refused"));
        _facebookProtector.Setup(p => p.Unprotect("enc-fb")).Throws(new System.Security.Cryptography.CryptographicException("key ring changed"));

        await Receipts().SendAsync(Owner, new ReadAdvance(_whatsAppConnection.Id, Customer, MessagingPlatform.WhatsApp, "whatsapp:wamid.IN1"), default);
        await Receipts().SendAsync(Owner, new ReadAdvance(_page.Id, "psid-1", MessagingPlatform.Facebook, "facebook:m_1"), default);
    }

    [Fact]
    public async Task AChannelThatNeverAnswersIsGivenUpOnQuickly()
    {
        _whatsApp.Setup(s => s.MarkReadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, string, CancellationToken>(async (_, _, _, ct) => await Task.Delay(Timeout.Infinite, ct));

        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        await Receipts().SendAsync(Owner, new ReadAdvance(_whatsAppConnection.Id, Customer, MessagingPlatform.WhatsApp, "whatsapp:wamid.IN1"), default);

        Assert.InRange(clock.Elapsed.TotalSeconds, 0, 6);
    }

    // What each sender puts on the wire, against a fake Meta.
    private sealed class FakeHandler(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Url { get; private set; }
        public string? Authorization { get; private set; }
        public string? Payload { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Url = request.RequestUri!.ToString();
            Authorization = request.Headers.Authorization!.ToString();
            Payload = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent("{\"error\":{\"code\":100}}", Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task WhatsAppSenderPostsAReadStatusForTheMessage()
    {
        FakeHandler handler = new();
        using HttpClient http = new(handler);

        await new WhatsAppSender(http, Options.Create(new WhatsAppSettings { ApiVersion = "v25.0" })).MarkReadAsync("123", "secret", "wamid.IN1", default);

        Assert.Equal("https://graph.facebook.com/v25.0/123/messages", handler.Url);
        Assert.Equal("Bearer secret", handler.Authorization);
        using JsonDocument json = JsonDocument.Parse(handler.Payload!);
        Assert.Equal("whatsapp", json.RootElement.GetProperty("messaging_product").GetString());
        Assert.Equal("read", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("wamid.IN1", json.RootElement.GetProperty("message_id").GetString());
    }

    [Fact]
    public async Task MessengerSenderSendsOnlyAMarkSeenAction()
    {
        FakeHandler handler = new();
        using HttpClient http = new(handler);

        await new FacebookSender(http, Options.Create(new FacebookSettings { ApiVersion = "v25.0" })).MarkSeenAsync("page-1", "secret", "psid-1", default);

        Assert.Equal("https://graph.facebook.com/v25.0/page-1/messages", handler.Url);
        Assert.Equal("Bearer secret", handler.Authorization);
        using JsonDocument json = JsonDocument.Parse(handler.Payload!);
        Assert.Equal("psid-1", json.RootElement.GetProperty("recipient").GetProperty("id").GetString());
        Assert.Equal("mark_seen", json.RootElement.GetProperty("sender_action").GetString());
        // Meta wants a sender action alone: no text or messaging type beside it.
        Assert.Equal(2, json.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public async Task InstagramSenderSendsOnlyAMarkSeenAction()
    {
        FakeHandler handler = new();
        using HttpClient http = new(handler);

        await new InstagramSender(http, Options.Create(new InstagramSettings { ApiVersion = "v25.0" })).MarkSeenAsync("ig-1", "secret", "igsid-1", default);

        Assert.Equal("https://graph.instagram.com/v25.0/ig-1/messages", handler.Url);
        Assert.Equal("Bearer secret", handler.Authorization);
        using JsonDocument json = JsonDocument.Parse(handler.Payload!);
        Assert.Equal("igsid-1", json.RootElement.GetProperty("recipient").GetProperty("id").GetString());
        Assert.Equal("mark_seen", json.RootElement.GetProperty("sender_action").GetString());
        Assert.Equal(2, json.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public async Task ARefusalFromAnyChannelSurfacesSoTheServiceCanLogIt()
    {
        FakeHandler handler = new(HttpStatusCode.BadRequest);
        using HttpClient http = new(handler);

        HttpRequestException whatsApp = await Assert.ThrowsAsync<HttpRequestException>(() =>
            new WhatsAppSender(http, Options.Create(new WhatsAppSettings())).MarkReadAsync("123", "secret", "wamid.IN1", default));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new FacebookSender(http, Options.Create(new FacebookSettings())).MarkSeenAsync("p", "secret", "psid", default));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new InstagramSender(http, Options.Create(new InstagramSettings())).MarkSeenAsync("a", "secret", "igsid", default));

        Assert.Contains("error 100", whatsApp.Message);
    }
}
