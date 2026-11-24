using System.Collections.Concurrent;
using Microsoft.AspNetCore.DataProtection;
using Moq;
using Parrot.Application.Conversations;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;
using Parrot.Domain.Entities;
using Parrot.Domain.Entities.Messages;
using Parrot.Domain.Enums;
using Parrot.Domain.Exceptions;
using Parrot.Infrastructure.SocialMediaIntegrations;
using Test.Parrot.Api.Tests.Services.Integrations;

namespace Test.Parrot.Api.Tests.Services;

public class TikTokReplyTests
{
    private const string Owner = "owner";
    private const string Business = "biz-open-id";
    private const string Customer = "customer-open-id";
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private readonly TikTokCredentialProtector _protector = new(new EphemeralDataProtectionProvider());
    private readonly MemoryTikTokRepository _repository = new();
    private readonly FakeTikTok _tikTok = new();
    private readonly ReplyStore _store = new();
    private readonly Mock<IConversationSequence> _sequence = new();
    private readonly FixedTimeProvider _time = new(Now);
    private readonly TikTokConnection _connection;

    public TikTokReplyTests()
    {
        _connection = new TikTokConnection
        {
            WorkspaceId = Owner, AppId = "7123456789", AppSecretEncrypted = _protector.Protect("app-secret"),
            TikTokOpenId = Business, DisplayName = "Parrot Test",
            AccessTokenEncrypted = _protector.Protect("access-token"), AccessTokenExpiresAt = Now.UtcDateTime.AddHours(12),
            RefreshTokenEncrypted = _protector.Protect("refresh-token"), RefreshTokenExpiresAt = Now.UtcDateTime.AddDays(20),
        };
        _repository.AddAsync(_connection);
        _store.LastIncoming = Incoming(Now.UtcDateTime.AddMinutes(-5));
        _sequence.Setup(s => s.NextAsync("tiktok:biz-open-id:customer-open-id", It.IsAny<CancellationToken>())).ReturnsAsync(7);
    }

    // ---- sending -------------------------------------------------------------

    [Fact]
    public async Task AReplyGoesIntoTheCustomersConversation_AndIsStoredAsOurOwn()
    {
        ConversationMessage reply = await Service().SendAsync(Snap(), Customer, Request("Thanks for asking!"), default);

        Assert.Equal("accepted", reply.Status);
        Assert.True(reply.Outgoing);
        Assert.Equal((Business, "access-token", "conversation-1", "Thanks for asking!"), Assert.Single(_tikTok.Sent));

        TikTokMessageDocument stored = Assert.IsType<TikTokMessageDocument>(Assert.Single(_store.Messages).Value);
        Assert.Equal("tiktok-message-1", stored.ProviderMessageId);
        Assert.Equal("conversation-1", stored.ConversationId);
        Assert.Equal(Customer, stored.From);
        Assert.Equal(Business, stored.ChannelId);
        Assert.Equal(MessagingPlatform.TikTok, stored.Platform);
        Assert.Equal(_connection.Id, stored.ConnectionId);
        Assert.Equal("tiktok:biz-open-id:customer-open-id", stored.ConversationKey);
        Assert.Equal(7, stored.Sequence);
        Assert.Equal(Now.UtcDateTime, stored.SentAt);
    }

    [Fact]
    public async Task ARepeatedRequest_IsAnsweredFromTheStoredReply_AndNeverResent()
    {
        TikTokReplyService service = Service();
        SendReplyRequest request = Request("Hello");

        await service.SendAsync(Snap(), Customer, request, default);
        ConversationMessage again = await service.SendAsync(Snap(), Customer, request, default);

        Assert.Equal("accepted", again.Status);
        Assert.Single(_tikTok.Sent);
        _sequence.Verify(s => s.NextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        await Assert.ThrowsAsync<ConflictException>(
            () => service.SendAsync(Snap(), Customer, request with { Text = "Something else" }, default));
    }

    [Fact]
    public async Task ARefusal_IsRecordedAsFailed_NotThrown_AndNotRetried()
    {
        _tikTok.SendFailure = new TikTokApiException(40020, "Reply window has closed.");
        TikTokReplyService service = Service();
        SendReplyRequest request = Request("Hello");

        Assert.Equal("failed", (await service.SendAsync(Snap(), Customer, request, default)).Status);
        Assert.Equal("failed", (await service.SendAsync(Snap(), Customer, request, default)).Status);
        Assert.Single(_tikTok.Sent);
    }

    [Theory]
    [InlineData(typeof(TaskCanceledException))]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(System.Text.Json.JsonException))]
    public async Task WhenItIsNotKnownWhetherTikTokGotIt_TheStatusIsUnknown_AndItIsNotResent(Type failure)
    {
        _tikTok.SendFailure = (Exception)Activator.CreateInstance(failure)!;
        TikTokReplyService service = Service();
        SendReplyRequest request = Request("Hello");

        Assert.Equal("unknown", (await service.SendAsync(Snap(), Customer, request, default)).Status);
        Assert.Equal("unknown", (await service.SendAsync(Snap(), Customer, request, default)).Status);
        Assert.Single(_tikTok.Sent);
    }

    // ---- the 48-hour window --------------------------------------------------

    [Theory]
    [InlineData(47, true)]
    [InlineData(49, false)]
    public async Task OnlyAMessageFromTheLast48Hours_OpensTheWindow(int hoursAgo, bool allowed)
    {
        _store.LastIncoming = Incoming(Now.UtcDateTime.AddHours(-hoursAgo));

        Task<ConversationMessage> send = Service().SendAsync(Snap(), Customer, Request("Hello"), default);

        if (allowed) Assert.Equal("accepted", (await send).Status);
        else await Assert.ThrowsAsync<ConflictException>(() => send);
        Assert.Equal(allowed ? 1 : 0, _tikTok.Sent.Count);
    }

    [Fact]
    public async Task WithNoIncomingMessage_OrOneWithoutAConversation_ThereIsNothingToReplyTo()
    {
        _store.LastIncoming = null;
        await Assert.ThrowsAsync<ConflictException>(() => Service().SendAsync(Snap(), Customer, Request("Hello"), default));

        _store.LastIncoming = Incoming(Now.UtcDateTime.AddMinutes(-5), conversationId: null);
        await Assert.ThrowsAsync<ConflictException>(() => Service().SendAsync(Snap(), Customer, Request("Hello"), default));

        Assert.Empty(_tikTok.Sent);
        Assert.Empty(_store.Messages);
    }

    // ---- what is accepted ----------------------------------------------------

    [Theory]
    [InlineData("customer open id", "Hello")]
    [InlineData("", "Hello")]
    [InlineData(Customer, "   ")]
    public async Task ABadRecipientOrText_IsRejected(string recipient, string text)
    {
        await Assert.ThrowsAsync<ValidationException>(
            () => Service().SendAsync(Snap(), recipient, Request(text), default));
        await Assert.ThrowsAsync<ValidationException>(
            () => Service().SendAsync(Snap(), Customer, new SendReplyRequest(Guid.Empty, "Hello"), default));
        await Assert.ThrowsAsync<ValidationException>(
            () => Service().SendAsync(Snap(), Customer, Request(new string('x', 4097)), default));
        Assert.Empty(_tikTok.Sent);
    }

    [Fact]
    public async Task ATikTokOpenIdThatIsNotANumber_IsAValidRecipient()
    {
        _store.LastIncoming = Incoming(Now.UtcDateTime.AddMinutes(-5), sender: "-000abc_DEF-123");
        _sequence.Setup(s => s.NextAsync("tiktok:-000abc_DEF-123:biz-open-id", It.IsAny<CancellationToken>())).ReturnsAsync(2);

        ConversationMessage reply = await Service().SendAsync(Snap(), "-000abc_DEF-123", Request("Hello"), default);

        Assert.Equal("accepted", reply.Status);
    }

    [Fact]
    public async Task AnAccountNotYetAuthorized_CannotSend()
    {
        _connection.TikTokOpenId = null;

        await Assert.ThrowsAsync<ConflictException>(() => Service().SendAsync(Snap(), Customer, Request("Hello"), default));
    }

    // ---- routing from ConversationService -----------------------------------

    [Fact]
    public async Task AReplyGoesToTikTokOnlyForTheOwnersTikTokConnection()
    {
        Mock<IWhatsAppConnectionRepository> whatsApp = new();
        ConversationService service = new(_store, whatsApp.Object, Mock.Of<IWhatsAppSender>(),
            new WhatsAppTokenProtector(new EphemeralDataProtectionProvider()), _sequence.Object,
            Mock.Of<IFacebookConnectionRepository>(), Mock.Of<IFacebookSender>(), Mock.Of<IFacebookCredentialProtector>(),
            Mock.Of<IInstagramConnectionRepository>(), Mock.Of<IInstagramSender>(), Mock.Of<IInstagramCredentialProtector>(), Service());

        // Not a phone number: WhatsApp's checks would refuse it.
        Assert.Equal("accepted", (await service.SendAsync(Owner, _connection.Id, Customer, Request("Hello"), default)).Status);
        Assert.Single(_tikTok.Sent);

        // Another connection, or another owner's, is not TikTok's to answer.
        await Assert.ThrowsAsync<ValidationException>(
            () => service.SendAsync(Owner, Guid.NewGuid(), Customer, Request("Hello"), default));
        await Assert.ThrowsAsync<ValidationException>(
            () => service.SendAsync("someone-else", _connection.Id, Customer, Request("Hello"), default));
        Assert.Single(_tikTok.Sent);
    }

    // The agent's own reply must not look like the team's: a team reply answers the agent's open questions.
    [Fact]
    public async Task TheAgentsReply_IsStoredAsTheAgents_AndAPersonsReplyIsNot()
    {
        ConversationService service = new(_store, Mock.Of<IWhatsAppConnectionRepository>(), Mock.Of<IWhatsAppSender>(),
            new WhatsAppTokenProtector(new EphemeralDataProtectionProvider()), _sequence.Object,
            Mock.Of<IFacebookConnectionRepository>(), Mock.Of<IFacebookSender>(), Mock.Of<IFacebookCredentialProtector>(),
            Mock.Of<IInstagramConnectionRepository>(), Mock.Of<IInstagramSender>(), Mock.Of<IInstagramCredentialProtector>(), Service());

        ConversationMessage agents = await service.SendAgentReplyAsync(Owner, _connection.Id, Customer, Request("I've passed this to the team."), default);
        ConversationMessage persons = await service.SendAsync(Owner, _connection.Id, Customer, Request("We deliver on Sundays."), default);

        Assert.True(_store.Messages.Values.Single(m => m.TextBody == "I've passed this to the team.").IsAiReply);
        Assert.Null(_store.Messages.Values.Single(m => m.TextBody == "We deliver on Sundays.").IsAiReply);
        Assert.Equal("ai", agents.Kind);
        Assert.Null(persons.Kind);
    }

    // ---- access tokens -------------------------------------------------------

    [Fact]
    public async Task AGoodToken_IsUsedAsIs()
    {
        Assert.Equal("access-token", await Tokens().GetAsync(Snap(), default));

        Assert.Equal(0, _tikTok.Refreshes);
    }

    [Fact]
    public async Task ATokenAboutToLapse_IsRenewed_AndTheRotatedTokensAreSavedEncrypted()
    {
        _connection.AccessTokenExpiresAt = Now.UtcDateTime.AddMinutes(4);

        string token = await Tokens().GetAsync(Snap(), default);

        Assert.Equal("refreshed-access-token", token);
        Assert.Equal(("7123456789", "app-secret", "refresh-token"), _tikTok.RefreshedWith);
        Assert.Equal("refreshed-access-token", _protector.Unprotect(_repository.Connection!.AccessTokenEncrypted!));
        Assert.Equal("rotated-refresh-token", _protector.Unprotect(_repository.Connection.RefreshTokenEncrypted!));
        Assert.Equal(Now.UtcDateTime.AddHours(24), _repository.Connection.AccessTokenExpiresAt);
        Assert.Equal(Now.UtcDateTime.AddDays(30), _repository.Connection.RefreshTokenExpiresAt);
    }

    [Fact]
    public async Task ALapsedAccessToken_IsRenewedBeforeASend()
    {
        _connection.AccessTokenExpiresAt = Now.UtcDateTime.AddHours(-3);

        ConversationMessage reply = await Service().SendAsync(Snap(), Customer, Request("Hello"), default);

        Assert.Equal("accepted", reply.Status);
        Assert.Equal("refreshed-access-token", Assert.Single(_tikTok.Sent).AccessToken);
    }

    [Fact]
    public async Task WhenTheRefreshTokenHasLapsedToo_TheOwnerMustAuthorizeAgain_AndNothingIsReserved()
    {
        _connection.AccessTokenExpiresAt = Now.UtcDateTime.AddHours(-3);
        _connection.RefreshTokenExpiresAt = Now.UtcDateTime.AddMinutes(-1);

        ConflictException error = await Assert.ThrowsAsync<ConflictException>(
            () => Service().SendAsync(Snap(), Customer, Request("Hello"), default));

        Assert.Contains("Authorize TikTok again", error.Message);
        Assert.Equal(0, _tikTok.Refreshes);
        Assert.Empty(_tikTok.Sent);
        Assert.Empty(_store.Messages);
    }

    [Fact]
    public async Task ARefusedRefresh_UsesTheTokensAnotherSendJustSaved()
    {
        _connection.AccessTokenExpiresAt = Now.UtcDateTime.AddMinutes(1);
        _tikTok.RefreshFailure = new TikTokApiException(40105, "Refresh token invalid.");
        _tikTok.DuringRefresh = RotateTokensAsAnotherSendWould;

        Assert.Equal("access-from-the-other-send", await Tokens().GetAsync(Snap(), default));
    }

    // A token inside the renewal margin but not yet lapsed still works, so a refused renewal falls back to it.
    [Fact]
    public async Task ARefusedRefresh_FallsBackToATokenThatHasNotYetLapsed()
    {
        _connection.AccessTokenExpiresAt = Now.UtcDateTime.AddMinutes(1);
        _tikTok.RefreshFailure = new TikTokApiException(40105, "Refresh token invalid.");

        Assert.Equal("access-token", await Tokens().GetAsync(Snap(), default));
    }

    [Fact]
    public async Task ARefusedRefresh_WithNoUsableTokenOnRecord_NeedsAuthorizing()
    {
        _connection.AccessTokenExpiresAt = Now.UtcDateTime.AddMinutes(-1);
        _tikTok.RefreshFailure = new TikTokApiException(40105, "Refresh token invalid.");

        await Assert.ThrowsAsync<ConflictException>(() => Tokens().GetAsync(Snap(), default));
    }

    // Two sends refresh together: the second to save loses, and uses what the first saved.
    [Fact]
    public async Task WhenAnotherSendSavedItsRefreshFirst_ItsTokensAreTheOnesUsed_NotOverwritten()
    {
        _connection.AccessTokenExpiresAt = Now.UtcDateTime.AddMinutes(1);
        _tikTok.DuringRefresh = RotateTokensAsAnotherSendWould;

        Assert.Equal("access-from-the-other-send", await Tokens().GetAsync(Snap(), default));
        Assert.Equal("access-from-the-other-send", _protector.Unprotect(_repository.Connection!.AccessTokenEncrypted!));
        Assert.Equal("refresh-from-the-other-send", _protector.Unprotect(_repository.Connection.RefreshTokenEncrypted!));
    }

    [Fact]
    public async Task WhenTikTokCannotBeReachedToRefresh_TheCallerCanRetry()
    {
        _connection.AccessTokenExpiresAt = Now.UtcDateTime.AddMinutes(1);
        _tikTok.RefreshFailure = new HttpRequestException("timeout");

        await Assert.ThrowsAsync<ServiceUnavailableException>(() => Tokens().GetAsync(Snap(), default));
    }

    [Fact]
    public async Task ACredentialThatCannotBeDecrypted_NeedsAuthorizing_NotACrash()
    {
        _connection.AccessTokenEncrypted = "not-protected-by-this-key-ring";

        await Assert.ThrowsAsync<ConflictException>(() => Tokens().GetAsync(Snap(), default));
    }

    [Fact]
    public async Task AConnectionNeverAuthorized_HasNoTokens()
    {
        TikTokConnection fresh = new() { WorkspaceId = Owner, AppId = "1", AppSecretEncrypted = _protector.Protect("s") };

        await Assert.ThrowsAsync<ConflictException>(() => Tokens().GetAsync(fresh, default));
    }

    // ---- helpers -------------------------------------------------------------

    private void RotateTokensAsAnotherSendWould()
    {
        _repository.Connection!.AccessTokenEncrypted = _protector.Protect("access-from-the-other-send");
        _repository.Connection.AccessTokenExpiresAt = Now.UtcDateTime.AddHours(24);
        _repository.Connection.RefreshTokenEncrypted = _protector.Protect("refresh-from-the-other-send");
        _repository.Connection.RefreshTokenExpiresAt = Now.UtcDateTime.AddDays(30);
    }

    // What the service is handed: a copy of the stored connection, so a change made while it works is not seen early.
    private TikTokConnection Snap()
    {
        TikTokConnection c = _repository.Connection!;
        return new TikTokConnection
        {
            Id = c.Id, WorkspaceId = c.WorkspaceId, WebhookIdentifier = c.WebhookIdentifier, AppId = c.AppId,
            AppSecretEncrypted = c.AppSecretEncrypted, TikTokOpenId = c.TikTokOpenId, DisplayName = c.DisplayName,
            AccessTokenEncrypted = c.AccessTokenEncrypted, AccessTokenExpiresAt = c.AccessTokenExpiresAt,
            RefreshTokenEncrypted = c.RefreshTokenEncrypted, RefreshTokenExpiresAt = c.RefreshTokenExpiresAt,
        };
    }

    private TikTokAccessTokens Tokens() => new(_repository, _protector, _tikTok, _time);

    private TikTokReplyService Service() => new(_store, _repository, Tokens(), _tikTok, _sequence.Object, _time);

    private static SendReplyRequest Request(string text) => new(Guid.NewGuid(), text);

    private static TikTokMessageDocument Incoming(DateTime receivedAt, string? conversationId = "conversation-1", string sender = Customer) => new()
    {
        MessageId = $"tiktok:{Guid.NewGuid():N}", OwnerUserId = Owner, Platform = MessagingPlatform.TikTok,
        ExternalAccountId = Business, ChannelId = Business, ChannelDisplayName = "", From = sender,
        Timestamp = "1", Type = "text", TextBody = "Hi", ReceivedAt = receivedAt, SentAt = receivedAt,
        ConversationId = conversationId, ConversationKey = ConversationKey.Create(MessagingPlatform.TikTok, sender, Business),
        Receiver = Business,
    };

    private sealed class ReplyStore : IConversationStore
    {
        public ConcurrentDictionary<string, MessageDocument> Messages { get; } = new();
        public MessageDocument? LastIncoming { get; set; }

        public Task<MessageDocument?> FindRequestAsync(string owner, Guid request, CancellationToken ct) =>
            Task.FromResult(Messages.GetValueOrDefault(ConversationService.RequestKey(owner, request)));
        public Task<bool> ReserveAsync(MessageDocument message, CancellationToken ct) => Task.FromResult(Messages.TryAdd(message.MessageId, message));
        public Task CompleteAsync(string id, string status, string? providerId, CancellationToken ct)
        {
            Messages[id].SendStatus = status;
            Messages[id].ProviderMessageId = providerId;
            return Task.CompletedTask;
        }
        public Task<bool> FailPendingAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task<MessageDocument?> LatestIncomingAsync(string owner, Guid connection, string sender, CancellationToken ct) => Task.FromResult(LastIncoming);
        public Task<IReadOnlyList<ConversationSummary>> ListAsync(string owner, int skip, int take, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ConversationSummary>> FindAsync(string owner, IReadOnlyCollection<(Guid Connection, string Sender)> conversations, CancellationToken ct) => throw new NotImplementedException();
        public Task<MessagePage> ReadAsync(string owner, Guid connection, string sender, string? before, CancellationToken ct) => throw new NotImplementedException();
        public Task<MediaPage> ListMediaAsync(string owner, Guid connection, string sender, string? before, CancellationToken ct) => throw new NotImplementedException();
    }
}
