using System.Collections.Concurrent;
using Microsoft.AspNetCore.DataProtection;
using Parrot.Infrastructure.SocialMediaIntegrations;
using Moq;
using Parrot.Application.Agents;
using Parrot.Application.Conversations;
using Parrot.Application.Integrations;
using Parrot.Application.Logging;
using Parrot.Application.Messaging;
using Parrot.Application.Storage;
using Parrot.Domain.Entities;
using Parrot.Domain.Entities.Messages;
using Parrot.Domain.Enums;
using Parrot.Domain.Exceptions;
using Test.Parrot.Api.Tests.Services.Messaging;

namespace Test.Parrot.Api.Tests.Services;

public class ConversationServiceTests
{
    private const string Owner = "owner";
    private const string Recipient = "2348012345678";
    private readonly WhatsAppConnection _connection = new()
    {
        WorkspaceId = Owner, Name = "Test", VerifyTokenEncrypted = "encrypted",
        PhoneNumberId = "123", WhatsAppBusinessAccountId = "456", WebhookVerifiedAt = DateTime.UtcNow,
    };
    private readonly Mock<IWhatsAppConnectionRepository> _connections = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly MemoryStore _store = new();
    private readonly CountingSequence _sequence = new();

    private readonly IWhatsAppTokenProtector _protector = new WhatsAppTokenProtector(new EphemeralDataProtectionProvider());

    // The Facebook Page a Messenger thread is on.
    private const string PageId = "185860297939218";
    private const string Psid = "6215837091842310";
    private const string PageConversation = "facebook:185860297939218:6215837091842310";
    private readonly FacebookConnection _page = new()
    {
        WorkspaceId = Owner, VerifyTokenEncrypted = "encrypted", PageId = PageId, PageName = "Oma's Kitchen",
        WebhookVerifiedAt = DateTime.UtcNow, AccessTokenEncrypted = "enc:page-token",
    };
    private readonly Mock<IFacebookConnectionRepository> _pages = new();
    private readonly Mock<IInstagramConnectionRepository> _instagramAccounts = new();
    private readonly Mock<IInstagramSender> _instagramSender = new();

    private sealed class AccountProtector : IInstagramCredentialProtector
    {
        public string Protect(string value) => $"enc:{value}";
        public string Unprotect(string protectedValue) => protectedValue[4..];
    }
    private readonly Mock<IFacebookSender> _facebookSender = new();
    private readonly Mock<IDeliveryReceipts> _receipts = new();

    private sealed class PageProtector : IFacebookCredentialProtector
    {
        public string Protect(string value) => $"enc:{value}";
        public string Unprotect(string protectedValue) => protectedValue[4..];
    }

    public ConversationServiceTests() => _connection.AccessTokenEncrypted = _protector.Protect("connection-token");

    private ConversationService Create()
    {
        _connections.Setup(x => x.GetByOwnerAndIdAsync(Owner, _connection.Id)).ReturnsAsync(_connection);
        _pages.Setup(x => x.GetByOwnerAsync(Owner, It.IsAny<CancellationToken>())).ReturnsAsync(_page);
        return new ConversationService(_store, _connections.Object, _sender.Object,
            _protector, _sequence, _pages.Object, _facebookSender.Object, new PageProtector(),
            _instagramAccounts.Object, _instagramSender.Object, new AccountProtector(), receipts: _receipts.Object);
    }

    // The customer's Messenger message a reply answers, as the worker stores it.
    private static MessageDocument PageIncoming(DateTime sentAt) => new FacebookMessageDocument
    {
        MessageId = "facebook:m_incoming", OwnerUserId = Owner, Platform = MessagingPlatform.Facebook,
        ExternalAccountId = PageId, ChannelId = PageId, ChannelDisplayName = "", From = Psid,
        Timestamp = "0", Type = "text", SentAt = sentAt, ReceivedAt = sentAt,
        ConversationKey = PageConversation, Sequence = 1, Receiver = PageId,
    };

    // Meta can report a reply delivered before the send has stored Meta's id for it.
    [Fact]
    public async Task AfterASend_ReceiptsThatArrivedEarlyAreApplied()
    {
        _store.LastIncoming = PageIncoming(DateTime.UtcNow);
        _facebookSender.Setup(x => x.SendAsync(PageId, "page-token", Psid, "Hi", It.IsAny<CancellationToken>())).ReturnsAsync("m_reply");

        await Create().SendAsync(Owner, _page.Id, Psid, new SendReplyRequest(Guid.NewGuid(), "Hi"), default);

        _receipts.Verify(x => x.ApplyParkedAsync(_page.Id, "m_reply", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ARejectedSend_HasNoChannelIdToApplyReceiptsTo()
    {
        _store.LastIncoming = PageIncoming(DateTime.UtcNow);
        _facebookSender.Setup(x => x.SendAsync(PageId, "page-token", Psid, "Hi", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FacebookRejectedException("10"));

        await Create().SendAsync(Owner, _page.Id, Psid, new SendReplyRequest(Guid.NewGuid(), "Hi"), default);

        _receipts.Verify(x => x.ApplyParkedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("accepted", false, false, "accepted")]
    [InlineData("accepted", true, false, "delivered")]
    [InlineData("accepted", true, true, "read")]
    [InlineData("accepted", false, true, "read")]
    [InlineData("failed", true, true, "failed")]
    [InlineData("unknown", false, false, "unknown")]
    public void TheStatusShownIsTheFurthestTheChannelHasReported(string sent, bool delivered, bool read, string shown)
    {
        MessageDocument doc = PageIncoming(DateTime.UtcNow);
        doc.Outgoing = true;
        doc.SendStatus = sent;
        doc.DeliveredAt = delivered ? DateTime.UtcNow : null;
        doc.ReadAt = read ? DateTime.UtcNow : null;

        Assert.Equal(shown, ConversationService.ToMessage(doc).Status);
    }

    [Fact]
    public void ACustomersMessageNeverShowsAReceipt()
    {
        MessageDocument doc = PageIncoming(DateTime.UtcNow);
        doc.ReadAt = DateTime.UtcNow;

        Assert.Equal("received", ConversationService.ToMessage(doc).Status);
    }

    [Fact]
    public async Task AMessengerReply_GoesThroughThePage_AndJoinsTheConversation()
    {
        _store.LastIncoming = PageIncoming(DateTime.UtcNow);
        _facebookSender.Setup(x => x.SendAsync(PageId, "page-token", Psid, "We have it in stock", It.IsAny<CancellationToken>()))
            .ReturnsAsync("m_reply");

        ConversationMessage sent = await Create().SendAsync(Owner, _page.Id, Psid, new SendReplyRequest(Guid.NewGuid(), "We have it in stock"), default);

        Assert.Equal("accepted", sent.Status);
        FacebookMessageDocument stored = Assert.IsType<FacebookMessageDocument>(_store.Messages[sent.Id]);
        Assert.Equal(MessagingPlatform.Facebook, stored.Platform);
        Assert.True(stored.Outgoing);
        Assert.Equal(Psid, stored.From);
        Assert.Equal(PageId, stored.Receiver);
        Assert.Equal(PageId, stored.ChannelId);
        Assert.Equal(PageConversation, stored.ConversationKey);
        Assert.Equal(2, stored.Sequence);
        Assert.Equal("m_reply", stored.ProviderMessageId);
        _sender.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AMessengerReply_IsSentOnce_ForARepeatedRequest()
    {
        _store.LastIncoming = PageIncoming(DateTime.UtcNow);
        _facebookSender.Setup(x => x.SendAsync(PageId, "page-token", Psid, "Hello", It.IsAny<CancellationToken>())).ReturnsAsync("m_reply");
        ConversationService service = Create();
        SendReplyRequest request = new(Guid.NewGuid(), "Hello");

        await service.SendAsync(Owner, _page.Id, Psid, request, default);
        await service.SendAsync(Owner, _page.Id, Psid, request, default);

        _facebookSender.Verify(x => x.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(1, _sequence.Calls);
    }

    [Fact]
    public async Task AMessengerReply_MetaRefuses_IsStoredAsFailed()
    {
        _store.LastIncoming = PageIncoming(DateTime.UtcNow);
        _facebookSender.Setup(x => x.SendAsync(PageId, "page-token", Psid, "Hello", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FacebookRejectedException("10"));

        ConversationMessage sent = await Create().SendAsync(Owner, _page.Id, Psid, new SendReplyRequest(Guid.NewGuid(), "Hello"), default);

        Assert.Equal("failed", sent.Status);
        Assert.Equal("failed", _store.Messages[sent.Id].SendStatus);
    }

    [Fact]
    public async Task AMessengerReply_TimingOut_IsUnknown_NotRetried()
    {
        _store.LastIncoming = PageIncoming(DateTime.UtcNow);
        _facebookSender.Setup(x => x.SendAsync(PageId, "page-token", Psid, "Hello", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("timeout"));

        ConversationMessage sent = await Create().SendAsync(Owner, _page.Id, Psid, new SendReplyRequest(Guid.NewGuid(), "Hello"), default);

        Assert.Equal("unknown", sent.Status);
        _facebookSender.Verify(x => x.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // Messenger's rule too: a free-form reply only within 24 hours of the customer's last message.
    [Fact]
    public async Task AMessengerReply_AfterTheWindow_IsRefusedBeforeSending()
    {
        _store.LastIncoming = PageIncoming(DateTime.UtcNow.AddHours(-25));

        await Assert.ThrowsAsync<ConflictException>(() =>
            Create().SendAsync(Owner, _page.Id, Psid, new SendReplyRequest(Guid.NewGuid(), "Hello"), default));
        _facebookSender.VerifyNoOtherCalls();
        Assert.Empty(_store.Messages);
    }

    [Fact]
    public async Task AMessengerReply_LongerThanMessengerAllows_IsRefused()
    {
        _store.LastIncoming = PageIncoming(DateTime.UtcNow);

        await Assert.ThrowsAsync<ValidationException>(() =>
            Create().SendAsync(Owner, _page.Id, Psid, new SendReplyRequest(Guid.NewGuid(), new string('a', 2001)), default));
        _facebookSender.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AMessengerReply_FromADisconnectedOrTokenlessPage_IsRefused()
    {
        _store.LastIncoming = PageIncoming(DateTime.UtcNow);
        _page.AccessTokenEncrypted = null;
        await Assert.ThrowsAsync<ConflictException>(() =>
            Create().SendAsync(Owner, _page.Id, Psid, new SendReplyRequest(Guid.NewGuid(), "Hello"), default));

        _page.AccessTokenEncrypted = "enc:page-token";
        _page.DisabledAt = DateTime.UtcNow;
        await Assert.ThrowsAsync<ConflictException>(() =>
            Create().SendAsync(Owner, _page.Id, Psid, new SendReplyRequest(Guid.NewGuid(), "Hello"), default));
        _facebookSender.VerifyNoOtherCalls();
    }

    // A reply refused with 190 means the token itself is no good: the Channels card should ask for a new one.
    [Theory]
    [InlineData("190", 1)]
    [InlineData("10", 0)]
    public async Task AnInstagramReply_RefusedFor190_FlagsTheToken(string code, int flagged)
    {
        InstagramConnection account = new()
        {
            WorkspaceId = Owner, VerifyTokenEncrypted = "enc:verify", AccountId = "17841400000000000", Username = "omaskitchen",
            WebhookVerifiedAt = DateTime.UtcNow, AccessTokenEncrypted = "enc:IGAAtoken",
        };
        _instagramAccounts.Setup(a => a.GetByOwnerAsync(Owner, It.IsAny<CancellationToken>())).ReturnsAsync(account);
        _store.LastIncoming = new InstagramMessageDocument
        {
            MessageId = "instagram:ig_1", OwnerUserId = Owner, Platform = MessagingPlatform.Instagram,
            ExternalAccountId = "17841400000000000", ChannelId = "17841400000000000", ChannelDisplayName = "", From = "5834920000000000",
            Timestamp = "0", Type = "text", SentAt = DateTime.UtcNow, ReceivedAt = DateTime.UtcNow,
            ConversationKey = "instagram:17841400000000000:5834920000000000", Sequence = 1, Receiver = "17841400000000000",
        };
        _instagramSender.Setup(s => s.SendAsync("17841400000000000", "IGAAtoken", "5834920000000000", "Hello", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InstagramRejectedException(code));

        ConversationMessage sent = await Create().SendAsync(Owner, account.Id, "5834920000000000", new SendReplyRequest(Guid.NewGuid(), "Hello"), default);

        Assert.Equal("failed", sent.Status);
        _instagramAccounts.Verify(a => a.MarkTokenRejectedAsync(account.Id, "enc:IGAAtoken", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Exactly(flagged));
    }

    [Fact]
    public async Task AReplyOnAConnectionThatIsNotTheOwners_IsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() =>
            Create().SendAsync(Owner, Guid.NewGuid(), Psid, new SendReplyRequest(Guid.NewGuid(), "Hello"), default));
        _facebookSender.VerifyNoOtherCalls();
        _sender.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ConcurrentAndRepeatedRequestsSendOnlyOnce()
    {
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _sender.Setup(x => x.SendAsync("123", "connection-token", Recipient, "Hello", It.IsAny<CancellationToken>())).Returns(release.Task);
        var service = Create();
        var request = new SendReplyRequest(Guid.NewGuid(), "Hello");
        var first = service.SendAsync(Owner, _connection.Id, Recipient, request, default);
        var second = await service.SendAsync(Owner, _connection.Id, Recipient, request, default);
        Assert.Equal("pending", second.Status);
        release.SetResult("wamid.unique");
        Assert.Equal("accepted", (await first).Status);
        Assert.Equal("accepted", (await service.SendAsync(Owner, _connection.Id, Recipient, request, default)).Status);
        _sender.Verify(x => x.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Single(_store.Messages);
        // A repeated request is answered from the stored reply and takes no new number.
        Assert.Equal(1, _sequence.Calls);
    }

    [Fact]
    public async Task TimeoutIsNotAutomaticallyResent()
    {
        _sender.Setup(x => x.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException());
        var service = Create();
        var request = new SendReplyRequest(Guid.NewGuid(), "Hello");
        Assert.Equal("unknown", (await service.SendAsync(Owner, _connection.Id, Recipient, request, default)).Status);
        Assert.Equal("unknown", (await service.SendAsync(Owner, _connection.Id, Recipient, request, default)).Status);
        _sender.Verify(x => x.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PersistenceFailureAfterMetaAcceptanceDoesNotResend()
    {
        _store.FailComplete = true;
        _sender.Setup(x => x.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("wamid.accepted");
        var service = Create();
        var request = new SendReplyRequest(Guid.NewGuid(), "Hello");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendAsync(Owner, _connection.Id, Recipient, request, default));
        Assert.Equal("pending", (await service.SendAsync(Owner, _connection.Id, Recipient, request, default)).Status);
        _sender.Verify(x => x.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SameRequestIdWithDifferentTextIsRejected()
    {
        _sender.Setup(x => x.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("wamid.accepted");
        var service = Create();
        var request = new SendReplyRequest(Guid.NewGuid(), "Hello");
        await service.SendAsync(Owner, _connection.Id, Recipient, request, default);
        await Assert.ThrowsAsync<ConflictException>(() => service.SendAsync(Owner, _connection.Id, Recipient, request with { Text = "Different" }, default));
    }

    [Fact]
    public async Task OtherOwnerCannotSend()
    {
        var service = Create();
        await Assert.ThrowsAsync<NotFoundException>(() => service.SendAsync("another-owner", _connection.Id, Recipient, new(Guid.NewGuid(), "Hi"), default));
        Assert.Empty(_store.Messages);
        _sender.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExpiredWindowCannotSend()
    {
        _store.LastIncoming = Incoming(DateTime.UtcNow.AddHours(-25));
        var service = Create();
        await Assert.ThrowsAsync<ConflictException>(() => service.SendAsync(Owner, _connection.Id, Recipient, new(Guid.NewGuid(), "Hi"), default));
        Assert.Empty(_store.Messages);
    }

    [Fact]
    public async Task MissingConnectionTokenCannotSend()
    {
        _connection.AccessTokenEncrypted = null;
        var service = Create();
        await Assert.ThrowsAsync<ConflictException>(() => service.SendAsync(Owner, _connection.Id, Recipient, new(Guid.NewGuid(), "Hi"), default));
        Assert.Empty(_store.Messages);
    }

    [Fact]
    public async Task ExplicitProviderRejectionIsStoredAsFailed()
    {
        _sender.Setup(x => x.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new WhatsAppRejectedException());
        var service = Create();
        Assert.Equal("failed", (await service.SendAsync(Owner, _connection.Id, Recipient, new(Guid.NewGuid(), "Hi"), default)).Status);
    }

    [Fact]
    public async Task UsesTheOwnedConnectionsTokenAndPhoneNumber()
    {
        _connection.PhoneNumberId = "789";
        _connection.AccessTokenEncrypted = _protector.Protect("another-connection-token");
        _sender.Setup(x => x.SendAsync("789", "another-connection-token", Recipient, "Hi", It.IsAny<CancellationToken>()))
            .ReturnsAsync("wamid.other");
        Assert.Equal("accepted", (await Create().SendAsync(Owner, _connection.Id, Recipient, new(Guid.NewGuid(), "Hi"), default)).Status);
        _sender.Verify(x => x.SendAsync("789", "another-connection-token", Recipient, "Hi", It.IsAny<CancellationToken>()), Times.Once);
        _sender.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UnreadableConnectionTokenDoesNotReserveOrSend()
    {
        _connection.AccessTokenEncrypted = "unreadable";
        await Assert.ThrowsAsync<ConflictException>(() => Create().SendAsync(Owner, _connection.Id, Recipient, new(Guid.NewGuid(), "Hi"), default));
        Assert.Empty(_store.Messages);
        _sender.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AReplyJoinsTheCustomersConversation_WithTheNextNumber()
    {
        _sender.Setup(x => x.SendAsync("123", "connection-token", Recipient, "Try 1234", It.IsAny<CancellationToken>())).ReturnsAsync("wamid.reply");

        ConversationMessage sent = await Create().SendAsync(Owner, _connection.Id, Recipient, new SendReplyRequest(Guid.NewGuid(), "Try 1234"), default);

        MessageDocument stored = _store.Messages[sent.Id];
        Assert.Equal(AdaConversation, stored.ConversationKey);
        Assert.Equal(2, stored.Sequence);
        Assert.Equal("2349011112222", stored.Receiver);
    }

    [Fact]
    public async Task AReplyToAMessageStoredBeforeTheWorker_HasNoNumber()
    {
        _store.LastIncoming = Incoming(DateTime.UtcNow, conversationKey: null);
        _sender.Setup(x => x.SendAsync(It.IsAny<string>(), It.IsAny<string>(), Recipient, "Hi", It.IsAny<CancellationToken>())).ReturnsAsync("wamid.reply");

        ConversationMessage sent = await Create().SendAsync(Owner, _connection.Id, Recipient, new SendReplyRequest(Guid.NewGuid(), "Hi"), default);

        Assert.Null(_store.Messages[sent.Id].ConversationKey);
        Assert.Null(_store.Messages[sent.Id].Sequence);
        Assert.Equal(0, _sequence.Calls);
    }

    private const string AdaConversation = "whatsapp:2348012345678:2349011112222";

    // The customer's message a reply answers, as the worker stores it.
    private static MessageDocument Incoming(DateTime sentAt, string? conversationKey = AdaConversation) => new WhatsAppMessageDocument
    {
        MessageId = "whatsapp:wamid.incoming", OwnerUserId = Owner, Platform = MessagingPlatform.WhatsApp,
        ExternalAccountId = "456", ChannelId = "123", ChannelDisplayName = "+234 901 111 2222", From = Recipient,
        Timestamp = "0", Type = "text", SentAt = sentAt, ReceivedAt = sentAt,
        ConversationKey = conversationKey, Sequence = conversationKey is null ? null : 1, Receiver = "2349011112222",
    };

    private sealed class CountingSequence : IConversationSequence
    {
        public int Calls;
        private long _value = 1;
        public Task<long> NextAsync(string conversationKey, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(Interlocked.Increment(ref _value));
        }
    }

    // --- Replying to a message (quoting it) ---

    // A message of this WhatsApp thread, stored as the worker or a send stores it.
    private WhatsAppMessageDocument Stored(string messageId, bool outgoing, string? providerId = null, string text = "Do you deliver to Yaba?") => new()
    {
        MessageId = messageId, OwnerUserId = Owner, ConnectionId = _connection.Id, Platform = MessagingPlatform.WhatsApp,
        ExternalAccountId = "456", ChannelId = "123", ChannelDisplayName = "", From = Recipient, Timestamp = "0",
        Type = "text", TextBody = text, ReceivedAt = DateTime.UtcNow, Outgoing = outgoing, ProviderMessageId = providerId,
        SendStatus = outgoing ? "accepted" : "received",
    };

    [Fact]
    public async Task ReplyingToACustomersMessage_QuotesItOnWhatsApp_AndKeepsTheQuoteWithTheReply()
    {
        _store.Messages["whatsapp:wamid.question"] = Stored("whatsapp:wamid.question", outgoing: false);
        _sender.Setup(x => x.SendReplyAsync("123", "connection-token", Recipient, "Yes, we do", "wamid.question", It.IsAny<CancellationToken>()))
            .ReturnsAsync("wamid.answer");

        ConversationMessage sent = await Create().SendAsync(Owner, _connection.Id, Recipient,
            new SendReplyRequest(Guid.NewGuid(), "Yes, we do") { ReplyToMessageId = "whatsapp:wamid.question" }, default);

        Assert.Equal("accepted", sent.Status);
        Assert.Equal(new MessageQuote("whatsapp:wamid.question", false, "Do you deliver to Yaba?", "text"), sent.ReplyTo);
        MessageReplyTo kept = _store.Messages[sent.Id].ReplyTo!;
        Assert.Equal("wamid.question", kept.ProviderMessageId);
        _sender.Verify(x => x.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReplyingToOurOwnReply_QuotesItByTheIdTheChannelGaveIt()
    {
        _store.Messages["outgoing:owner:earlier"] = Stored("outgoing:owner:earlier", outgoing: true, providerId: "wamid.earlier", text: "Our menu is attached");
        _sender.Setup(x => x.SendReplyAsync("123", "connection-token", Recipient, "Prices changed", "wamid.earlier", It.IsAny<CancellationToken>()))
            .ReturnsAsync("wamid.answer");

        ConversationMessage sent = await Create().SendAsync(Owner, _connection.Id, Recipient,
            new SendReplyRequest(Guid.NewGuid(), "Prices changed") { ReplyToMessageId = "outgoing:owner:earlier" }, default);

        Assert.True(sent.ReplyTo!.Outgoing);
        Assert.Equal("Our menu is attached", sent.ReplyTo.Text);
    }

    [Fact]
    public async Task OnMessenger_TheQuoteGoesAsReplyTo()
    {
        MessageDocument incoming = PageIncoming(DateTime.UtcNow);
        incoming.ConnectionId = _page.Id;
        incoming.TextBody = "Is it spicy?";
        _store.LastIncoming = incoming;
        _store.Messages[incoming.MessageId] = incoming;
        _facebookSender.Setup(x => x.SendReplyAsync(PageId, "page-token", Psid, "A little", "m_incoming", It.IsAny<CancellationToken>()))
            .ReturnsAsync("m_answer");

        ConversationMessage sent = await Create().SendAsync(Owner, _page.Id, Psid,
            new SendReplyRequest(Guid.NewGuid(), "A little") { ReplyToMessageId = "facebook:m_incoming" }, default);

        Assert.Equal("accepted", sent.Status);
        Assert.Equal("Is it spicy?", sent.ReplyTo!.Text);
    }

    [Fact]
    public async Task AQuoteFromAnotherConversation_IsNotFound_AndNothingIsSent()
    {
        WhatsAppMessageDocument elsewhere = Stored("whatsapp:wamid.other", outgoing: false);
        elsewhere.From = "2348099999999";
        _store.Messages[elsewhere.MessageId] = elsewhere;

        await Assert.ThrowsAsync<NotFoundException>(() => Create().SendAsync(Owner, _connection.Id, Recipient,
            new SendReplyRequest(Guid.NewGuid(), "Hi") { ReplyToMessageId = "whatsapp:wamid.other" }, default));

        Assert.DoesNotContain(_store.Messages.Values, m => m.Outgoing);
    }

    [Fact]
    public async Task AReplyThatNeverReachedTheChannel_CannotBeQuoted()
    {
        _store.Messages["outgoing:owner:failed"] = Stored("outgoing:owner:failed", outgoing: true, providerId: null);

        await Assert.ThrowsAsync<ConflictException>(() => Create().SendAsync(Owner, _connection.Id, Recipient,
            new SendReplyRequest(Guid.NewGuid(), "Hi") { ReplyToMessageId = "outgoing:owner:failed" }, default));
    }

    [Fact]
    public async Task APlainReply_StillGoesThroughThePlainSend()
    {
        _sender.Setup(x => x.SendAsync("123", "connection-token", Recipient, "Hello", It.IsAny<CancellationToken>())).ReturnsAsync("wamid.plain");

        ConversationMessage sent = await Create().SendAsync(Owner, _connection.Id, Recipient, new SendReplyRequest(Guid.NewGuid(), "Hello"), default);

        Assert.Null(sent.ReplyTo);
        _sender.Verify(x => x.SendReplyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AFileCanReplyToAMessageToo()
    {
        _store.Messages["whatsapp:wamid.question"] = Stored("whatsapp:wamid.question", outgoing: false);
        _sender.Setup(x => x.SendMediaReplyAsync("123", "connection-token", Recipient, "image", "image/png", "menu.png",
            It.IsAny<Stream>(), null, "wamid.question", It.IsAny<CancellationToken>())).ReturnsAsync("wamid.file");

        ConversationMessage sent = await CreateWithStorage().SendFileAsync(Owner, _connection.Id, Recipient, Guid.NewGuid(), null,
            File("menu.png", Png), default, "whatsapp:wamid.question");

        Assert.Equal("accepted", sent.Status);
        Assert.Equal("whatsapp:wamid.question", sent.ReplyTo!.Id);
    }

    private sealed class MemoryStore : IConversationStore
    {
        public ConcurrentDictionary<string, MessageDocument> Messages { get; } = new();
        public MessageDocument? LastIncoming { get; set; } = Incoming(DateTime.UtcNow);
        public bool FailComplete { get; set; }
        public Task<MessageDocument?> FindRequestAsync(string owner, Guid request, CancellationToken ct) =>
            Task.FromResult(Messages.GetValueOrDefault(ConversationService.RequestKey(owner, request)));
        public Task<bool> ReserveAsync(MessageDocument message, CancellationToken ct) => Task.FromResult(Messages.TryAdd(message.MessageId, message));
        public Task CompleteAsync(string id, string status, string? providerId, CancellationToken ct)
        {
            if (FailComplete) throw new InvalidOperationException("Mongo unavailable");
            Messages[id].SendStatus = status; Messages[id].ProviderMessageId = providerId;
            return Task.CompletedTask;
        }
        public Task<MessageDocument?> LatestIncomingAsync(string owner, Guid connection, string sender, CancellationToken ct) => Task.FromResult(LastIncoming);
        public Task<bool> FailPendingAsync(string id, CancellationToken ct)
        {
            if (Messages[id].SendStatus != "pending") return Task.FromResult(false);
            Messages[id].SendStatus = "failed";
            return Task.FromResult(true);
        }
        public Task<MessageDocument?> FindMessageAsync(string owner, Guid connection, string sender, string messageId, CancellationToken ct) =>
            Task.FromResult(Messages.TryGetValue(messageId, out MessageDocument? found)
                && found.OwnerUserId == owner && found.ConnectionId == connection && found.From == sender ? found : null);
        public Task<IReadOnlyList<ConversationSummary>> ListAsync(string owner, int skip, int take, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ConversationSummary>> FindAsync(string owner, IReadOnlyCollection<(Guid Connection, string Sender)> conversations, CancellationToken ct) => throw new NotImplementedException();
        public Task<MessagePage> ReadAsync(string owner, Guid connection, string sender, string? before, CancellationToken ct) => throw new NotImplementedException();
        public Task<MediaPage> ListMediaAsync(string owner, Guid connection, string sender, string? before, CancellationToken ct) => throw new NotImplementedException();
    }

    // --- Files the business attaches ---

    private readonly Mock<IPrivateObjectStorage> _media = new();

    private ConversationService CreateWithStorage()
    {
        _connections.Setup(x => x.GetByOwnerAndIdAsync(Owner, _connection.Id)).ReturnsAsync(_connection);
        _pages.Setup(x => x.GetByOwnerAsync(Owner, It.IsAny<CancellationToken>())).ReturnsAsync(_page);
        return new ConversationService(_store, _connections.Object, _sender.Object,
            _protector, _sequence, _pages.Object, _facebookSender.Object, new PageProtector(),
            _instagramAccounts.Object, _instagramSender.Object, new AccountProtector(), receipts: _receipts.Object, mediaStorage: _media.Object);
    }

    private static OutgoingFile File(string name, params byte[] bytes) => new(name, "ignored/by-server", bytes.Length, () => new MemoryStream(bytes));

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    [Fact]
    public async Task APictureForAWhatsAppCustomer_IsKept_SentWithItsCaption_AndShownInTheThread()
    {
        _sender.Setup(x => x.SendMediaAsync("123", "connection-token", Recipient, "image", "image/png", "menu.png",
            It.IsAny<Stream>(), "Today's menu", It.IsAny<CancellationToken>())).ReturnsAsync("wamid.file");

        ConversationMessage sent = await CreateWithStorage().SendFileAsync(Owner, _connection.Id, Recipient, Guid.NewGuid(), " Today's menu ", File("menu.png", Png), default);

        Assert.Equal("accepted", sent.Status);
        Assert.Equal("Today's menu", sent.Text);
        MessageAttachment kept = Assert.Single(_store.Messages[sent.Id].Attachments!);
        Assert.Equal("image", kept.Kind);
        Assert.StartsWith($"business-media/{Owner}/", kept.StorageKey);
        _media.Verify(x => x.PutAsync(kept.StorageKey!, It.IsAny<Stream>(), "image/png", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AFileWhoseBytesAreNotWhatItsNameSays_IsRefusedBeforeAnythingIsSent()
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            CreateWithStorage().SendFileAsync(Owner, _connection.Id, Recipient, Guid.NewGuid(), null, File("virus.png", 0x4D, 0x5A, 0x90), default));

        _sender.Verify(x => x.SendMediaAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("setup.exe")]
    [InlineData("noextension")]
    [InlineData("page.html")]
    public async Task OnlyPicturesAndDocumentsMayBeAttached(string name) =>
        await Assert.ThrowsAsync<ValidationException>(() =>
            CreateWithStorage().SendFileAsync(Owner, _connection.Id, Recipient, Guid.NewGuid(), null, File(name, 1, 2, 3), default));

    [Fact]
    public async Task APictureOverFiveMegabytes_IsRefused()
    {
        OutgoingFile big = new("big.png", "image/png", 5 * 1024 * 1024 + 1, () => new MemoryStream(Png));

        await Assert.ThrowsAsync<ValidationException>(() =>
            CreateWithStorage().SendFileAsync(Owner, _connection.Id, Recipient, Guid.NewGuid(), null, big, default));
    }

    [Fact]
    public async Task ARetryWithTheSameRequestId_DoesNotSendTheFileTwice()
    {
        _sender.Setup(x => x.SendMediaAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("wamid.file");
        ConversationService service = CreateWithStorage();
        Guid request = Guid.NewGuid();

        ConversationMessage first = await service.SendFileAsync(Owner, _connection.Id, Recipient, request, "Hi", File("a.png", Png), default);
        ConversationMessage again = await service.SendFileAsync(Owner, _connection.Id, Recipient, request, "Hi", File("a.png", Png), default);

        Assert.Equal(first.Id, again.Id);
        _sender.Verify(x => x.SendMediaAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task APictureForAMessengerCustomer_GoesThroughThePage_AndTakesNoCaption()
    {
        _store.LastIncoming = PageIncoming(DateTime.UtcNow);
        _facebookSender.Setup(x => x.SendMediaAsync(PageId, "page-token", Psid, "file", "application/pdf", "invoice.pdf",
            It.IsAny<Stream>(), It.IsAny<CancellationToken>())).ReturnsAsync("m_file");
        ConversationService service = CreateWithStorage();

        await Assert.ThrowsAsync<ValidationException>(() =>
            service.SendFileAsync(Owner, _page.Id, Psid, Guid.NewGuid(), "see attached", File("invoice.pdf", 0x25, 0x50, 0x44, 0x46, 0x2D), default));
        ConversationMessage sent = await service.SendFileAsync(Owner, _page.Id, Psid, Guid.NewGuid(), null, File("invoice.pdf", 0x25, 0x50, 0x44, 0x46, 0x2D), default);

        Assert.Equal("accepted", sent.Status);
        Assert.Equal("document", Assert.Single(sent.Attachments!).Kind);
    }

    [Fact]
    public async Task TheSameRequestIdWithAnotherFile_IsAMistakeNotARetry()
    {
        _sender.Setup(x => x.SendMediaAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("wamid.file");
        ConversationService service = CreateWithStorage();
        Guid request = Guid.NewGuid();
        await service.SendFileAsync(Owner, _connection.Id, Recipient, request, null, File("a.png", Png), default);

        await Assert.ThrowsAsync<ConflictException>(() =>
            service.SendFileAsync(Owner, _connection.Id, Recipient, request, null, File("b.png", Png), default));
    }

    [Fact]
    public async Task WhenTheCopyCannotBeKept_NothingIsSentToTheCustomer()
    {
        _media.Setup(x => x.PutAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("bucket unavailable"));

        ConversationMessage sent = await CreateWithStorage().SendFileAsync(Owner, _connection.Id, Recipient, Guid.NewGuid(), null, File("a.png", Png), default);

        Assert.Equal("failed", sent.Status);
        _sender.Verify(x => x.SendMediaAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // --- Files queued for the send worker ---

    private sealed class Jobs : IOutboundJobPublisher
    {
        public List<OutboundJob> Published { get; } = new();
        public Exception? Failure { get; set; }
        public Action? BeforeFailing { get; set; }

        public Task PublishAsync(OutboundJob job, CancellationToken cancellationToken = default)
        {
            if (Failure is not null)
            {
                BeforeFailing?.Invoke();
                throw Failure;
            }
            Published.Add(job);
            return Task.CompletedTask;
        }
    }

    private ConversationService CreateQueued(Jobs jobs, bool defer = true)
    {
        _connections.Setup(x => x.GetByOwnerAndIdAsync(Owner, _connection.Id)).ReturnsAsync(_connection);
        _pages.Setup(x => x.GetByOwnerAsync(Owner, It.IsAny<CancellationToken>())).ReturnsAsync(_page);
        return new ConversationService(_store, _connections.Object, _sender.Object,
            _protector, _sequence, _pages.Object, _facebookSender.Object, new PageProtector(),
            _instagramAccounts.Object, _instagramSender.Object, new AccountProtector(), receipts: _receipts.Object, mediaStorage: _media.Object,
            outboundJobs: jobs, outboundSettings: Microsoft.Extensions.Options.Options.Create(new OutboundSettings { DeferFileSends = defer }));
    }

    private void VerifyNothingSent() => _sender.Verify(x => x.SendMediaAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
        It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

    [Fact]
    public async Task WhenQueued_APicture_IsKept_ReservedPending_AndHandedToTheWorker_WithoutBeingSentByTheRequest()
    {
        Jobs jobs = new();

        ConversationMessage sent = await CreateQueued(jobs).SendFileAsync(Owner, _connection.Id, Recipient, Guid.NewGuid(), "Menu", File("menu.png", Png), default);

        Assert.Equal("pending", sent.Status);
        MessageAttachment kept = Assert.Single(_store.Messages[sent.Id].Attachments!);
        _media.Verify(x => x.PutAsync(kept.StorageKey!, It.IsAny<Stream>(), "image/png", It.IsAny<CancellationToken>()), Times.Once);
        OutboundJob job = Assert.Single(jobs.Published);
        Assert.Equal(sent.Id, job.MessageId);
        Assert.Equal(OutboundJob.KeyFor(_connection.Id, Recipient), job.ThreadKey);
        VerifyNothingSent();
    }

    [Fact]
    public async Task WhenQueued_APictureForAMessengerCustomer_IsQueuedToo()
    {
        _store.LastIncoming = PageIncoming(DateTime.UtcNow);
        Jobs jobs = new();

        ConversationMessage sent = await CreateQueued(jobs).SendFileAsync(Owner, _page.Id, Psid, Guid.NewGuid(), null, File("invoice.pdf", 0x25, 0x50, 0x44, 0x46, 0x2D), default);

        Assert.Equal("pending", sent.Status);
        Assert.Equal(OutboundJob.KeyFor(_page.Id, Psid), Assert.Single(jobs.Published).ThreadKey);
    }

    [Fact]
    public async Task WhenQueued_ARetryWithTheSameRequestId_QueuesNothingMore()
    {
        Jobs jobs = new();
        ConversationService service = CreateQueued(jobs);
        Guid request = Guid.NewGuid();

        ConversationMessage first = await service.SendFileAsync(Owner, _connection.Id, Recipient, request, null, File("a.png", Png), default);
        ConversationMessage again = await service.SendFileAsync(Owner, _connection.Id, Recipient, request, null, File("a.png", Png), default);

        Assert.Equal(first.Id, again.Id);
        Assert.Single(jobs.Published);
    }

    [Fact]
    public async Task WhenQueued_ARetryOfAReplyLeftPendingForAWhile_QueuesItAgainBecauseItsJobWasLost()
    {
        Jobs jobs = new();
        ConversationService service = CreateQueued(jobs);
        Guid request = Guid.NewGuid();
        ConversationMessage first = await service.SendFileAsync(Owner, _connection.Id, Recipient, request, null, File("a.png", Png), default);
        _store.Messages[first.Id].ReceivedAt = DateTime.UtcNow.AddMinutes(-2);

        await service.SendFileAsync(Owner, _connection.Id, Recipient, request, null, File("a.png", Png), default);

        Assert.Equal(2, jobs.Published.Count);
    }

    [Fact]
    public async Task WhenQueued_AReplyTheWorkerHasAlreadySettled_IsNotQueuedAgainByARetry()
    {
        Jobs jobs = new();
        ConversationService service = CreateQueued(jobs);
        Guid request = Guid.NewGuid();
        ConversationMessage first = await service.SendFileAsync(Owner, _connection.Id, Recipient, request, null, File("a.png", Png), default);
        _store.Messages[first.Id].ReceivedAt = DateTime.UtcNow.AddMinutes(-2);
        _store.Messages[first.Id].SendStatus = "accepted";

        ConversationMessage again = await service.SendFileAsync(Owner, _connection.Id, Recipient, request, null, File("a.png", Png), default);

        Assert.Equal("accepted", again.Status);
        Assert.Single(jobs.Published);
    }

    [Fact]
    public async Task WhenQueued_ACopyThatCannotBeKept_FailsTheReplyCleanly_AndQueuesNothing()
    {
        _media.Setup(x => x.PutAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("bucket unavailable"));
        Jobs jobs = new();

        ConversationMessage sent = await CreateQueued(jobs).SendFileAsync(Owner, _connection.Id, Recipient, Guid.NewGuid(), null, File("a.png", Png), default);

        Assert.Equal("failed", sent.Status);
        Assert.Empty(jobs.Published);
        VerifyNothingSent();
    }

    [Fact]
    public async Task WhenQueued_AJobKafkaWillNotTake_FailsTheReplyCleanly()
    {
        Jobs jobs = new() { Failure = new IOException("Kafka is down") };

        ConversationMessage sent = await CreateQueued(jobs).SendFileAsync(Owner, _connection.Id, Recipient, Guid.NewGuid(), null, File("a.png", Png), default);

        Assert.Equal("failed", sent.Status);
        Assert.Equal("failed", _store.Messages[sent.Id].SendStatus);
        VerifyNothingSent();
    }

    [Fact]
    public async Task WhenQueued_AJobThatReachedKafkaDespiteTheError_IsNotOverwrittenAsFailedOnceTheWorkerHasIt()
    {
        Jobs jobs = new() { Failure = new TimeoutException("ack lost") };
        jobs.BeforeFailing = () => _store.Messages.Values.Single().SendStatus = "sending";

        ConversationMessage sent = await CreateQueued(jobs).SendFileAsync(Owner, _connection.Id, Recipient, Guid.NewGuid(), null, File("a.png", Png), default);

        Assert.Equal("pending", sent.Status);
        Assert.Equal("sending", _store.Messages[sent.Id].SendStatus);
    }

    [Fact]
    public async Task WhenQueued_APictureThatQuotesAMessage_KeepsTheQuoteOnTheReplySoTheWorkerCanSendIt()
    {
        _store.Messages["whatsapp:wamid.question"] = Stored("whatsapp:wamid.question", outgoing: false);
        Jobs jobs = new();

        ConversationMessage sent = await CreateQueued(jobs).SendFileAsync(Owner, _connection.Id, Recipient, Guid.NewGuid(), null, File("menu.png", Png), default,
            replyToMessageId: "whatsapp:wamid.question");

        Assert.Equal("pending", sent.Status);
        Assert.Equal("wamid.question", _store.Messages[sent.Id].ReplyTo!.ProviderMessageId);
        Assert.Single(jobs.Published);
        VerifyNothingSent();
    }

    [Fact]
    public async Task WithTheSwitchOffTheRequestStillSendsItselfEvenWhenAPublisherIsWired()
    {
        _sender.Setup(x => x.SendMediaAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("wamid.file");
        Jobs jobs = new();

        ConversationMessage sent = await CreateQueued(jobs, defer: false).SendFileAsync(Owner, _connection.Id, Recipient, Guid.NewGuid(), null, File("a.png", Png), default);

        Assert.Equal("accepted", sent.Status);
        Assert.Empty(jobs.Published);
    }

    private sealed class FakeHumanTakeoverStore : IHumanTakeoverStore
    {
        private readonly HashSet<(string Owner, Guid Connection, string Sender)> _active = new();

        public List<(string Owner, Guid Connection, string Sender)> SetCalls { get; } = new();
        public List<(string Owner, Guid Connection, string Sender)> ClearCalls { get; } = new();

        public Task SetAsync(string owner, Guid connection, string sender, DateTime? controlUntil, CancellationToken ct)
        {
            SetCalls.Add((owner, connection, sender));
            _active.Add((owner, connection, sender));
            return Task.CompletedTask;
        }

        public Task ClearAsync(string owner, Guid connection, string sender, CancellationToken ct)
        {
            ClearCalls.Add((owner, connection, sender));
            _active.Remove((owner, connection, sender));
            return Task.CompletedTask;
        }

        public List<(string Owner, Guid Connection, string Sender)> TouchCalls { get; } = new();

        public Task TouchAsync(string owner, Guid connection, string sender, CancellationToken ct)
        {
            TouchCalls.Add((owner, connection, sender));
            _active.Add((owner, connection, sender));
            return Task.CompletedTask;
        }

        public Task<bool> IsActiveAsync(string owner, Guid connection, string sender, CancellationToken ct) =>
            Task.FromResult(_active.Contains((owner, connection, sender)));

        public Task<IReadOnlyDictionary<(Guid Connection, string Sender), DateTime?>> GetActiveAsync(
            string owner, IReadOnlyList<(Guid Connection, string Sender)> conversations, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<(Guid, string), DateTime?>>(new Dictionary<(Guid, string), DateTime?>());
    }

    private sealed class NullAppLogger : IAppLogger<ConversationService>
    {
        public void LogDebug(string message, params object?[] args) { }
        public void LogInformation(string message, params object?[] args) { }
        public void LogWarning(string message, params object?[] args) { }
        public void LogWarning(Exception exception, string message, params object?[] args) { }
        public void LogError(string message, params object?[] args) { }
        public void LogError(Exception exception, string message, params object?[] args) { }
        public IDisposable BeginEntityScope(string entityType, string entityId) => new NullScope();
        private sealed class NullScope : IDisposable { public void Dispose() { } }
    }

    private sealed class FakeMessagePersistedPublisher : IMessagePersistedPublisher
    {
        public List<MessagePersisted> Published { get; } = new();
        public Task PublishAsync(MessagePersisted persisted, CancellationToken cancellationToken = default)
        {
            Published.Add(persisted);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTenantIdLookup(string? ownerId) : ITenantIdLookup
    {
        public Task<string?> FindOwnerIdAsync(string workspaceId, CancellationToken ct) =>
            Task.FromResult(ownerId);
    }

    private ConversationService CreateWithTakeover(FakeHumanTakeoverStore store)
    {
        _connections.Setup(x => x.GetByOwnerAndIdAsync(Owner, _connection.Id)).ReturnsAsync(_connection);
        return new ConversationService(_store, _connections.Object, _sender.Object,
            _protector, _sequence, _pages.Object, _facebookSender.Object, new PageProtector(),
            _instagramAccounts.Object, _instagramSender.Object, new AccountProtector(),
            receipts: _receipts.Object, humanTakeover: store, logger: new NullAppLogger());
    }

    private ConversationService CreateForAgentReply(FakeMessagePersistedPublisher publisher, FakeHumanTakeoverStore? takeover = null)
    {
        _connections.Setup(x => x.GetByOwnerAndIdAsync(Owner, _connection.Id)).ReturnsAsync(_connection);
        return new ConversationService(_store, _connections.Object, _sender.Object,
            _protector, _sequence, _pages.Object, _facebookSender.Object, new PageProtector(),
            _instagramAccounts.Object, _instagramSender.Object, new AccountProtector(),
            receipts: _receipts.Object, humanTakeover: takeover, logger: new NullAppLogger(),
            publisher: publisher, tenantLookup: new FakeTenantIdLookup("root-account-1"));
    }

    [Fact]
    public async Task SendAsync_AfterSuccessfulSend_SetsTakeoverFlag()
    {
        _sender.Setup(x => x.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("wamid.reply");
        FakeHumanTakeoverStore takeover = new();

        await CreateWithTakeover(takeover).SendAsync(Owner, _connection.Id, Recipient,
            new SendReplyRequest(Guid.NewGuid(), "Hello"), default);

        Assert.Single(takeover.TouchCalls);
        (string owner, Guid connection, string sender) = takeover.TouchCalls[0];
        Assert.Equal(Owner, owner);
        Assert.Equal(_connection.Id, connection);
        Assert.Equal(Recipient, sender);
    }

    [Fact]
    public async Task SendAsync_WhenConnectionNotFound_DoesNotSetTakeoverFlag()
    {
        _connections.Setup(x => x.GetByOwnerAndIdAsync(Owner, _connection.Id)).ReturnsAsync((WhatsAppConnection?)null);
        _pages.Setup(x => x.GetByOwnerAsync(Owner, It.IsAny<CancellationToken>())).ReturnsAsync((FacebookConnection?)null);
        _instagramAccounts.Setup(x => x.GetByOwnerAsync(Owner, It.IsAny<CancellationToken>())).ReturnsAsync((InstagramConnection?)null);
        FakeHumanTakeoverStore takeover = new();
        ConversationService service = new ConversationService(_store, _connections.Object, _sender.Object,
            _protector, _sequence, _pages.Object, _facebookSender.Object, new PageProtector(),
            _instagramAccounts.Object, _instagramSender.Object, new AccountProtector(),
            humanTakeover: takeover, logger: new NullAppLogger());

        await Assert.ThrowsAsync<NotFoundException>(() =>
            service.SendAsync(Owner, _connection.Id, Recipient, new SendReplyRequest(Guid.NewGuid(), "Hi"), default));

        Assert.Empty(takeover.SetCalls);
        Assert.Empty(takeover.TouchCalls);
    }

    [Fact]
    public async Task SendAgentReplyAsync_PublishesAgentToUserEvent()
    {
        _sender.Setup(x => x.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("wamid.agent-reply");
        FakeMessagePersistedPublisher publisher = new();

        await CreateForAgentReply(publisher).SendAgentReplyAsync(Owner, _connection.Id, Recipient,
            new SendReplyRequest(Guid.NewGuid(), "Here's the answer"), default);

        MessagePersisted published = Assert.Single(publisher.Published);
        Assert.Equal(CommsType.AgentToUser, published.Data.CommsType);
        Assert.Equal("root-account-1", published.Data.TenantId);
        Assert.Equal(Owner, published.Data.WorkspaceId);
        Assert.True(published.Data.Outgoing);
    }

    [Fact]
    public async Task SendAgentReplyAsync_DoesNotSetHumanTakeoverFlag()
    {
        _sender.Setup(x => x.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("wamid.agent-reply");
        FakeHumanTakeoverStore takeover = new();
        FakeMessagePersistedPublisher publisher = new();

        await CreateForAgentReply(publisher, takeover).SendAgentReplyAsync(Owner, _connection.Id, Recipient,
            new SendReplyRequest(Guid.NewGuid(), "Here's the answer"), default);

        Assert.Empty(takeover.SetCalls);
        Assert.Empty(takeover.TouchCalls);
    }

    // --- A teammate's reply reaches Omni-AI as TenantToUser ---

    private sealed class FailingPublisher : IMessagePersistedPublisher
    {
        public Task PublishAsync(MessagePersisted persisted, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Kafka is down");
    }

    private ConversationService CreateWithPublisher(IMessagePersistedPublisher publisher, IMediaLinkSigner? mediaLinks = null)
    {
        _connections.Setup(x => x.GetByOwnerAndIdAsync(Owner, _connection.Id)).ReturnsAsync(_connection);
        return new ConversationService(_store, _connections.Object, _sender.Object,
            _protector, _sequence, _pages.Object, _facebookSender.Object, new PageProtector(),
            _instagramAccounts.Object, _instagramSender.Object, new AccountProtector(),
            receipts: _receipts.Object, mediaStorage: _media.Object, logger: new NullAppLogger(),
            publisher: publisher, tenantLookup: new FakeTenantIdLookup("root-account-1"), mediaLinks: mediaLinks);
    }

    [Fact]
    public async Task SendAsync_FromATeammate_PublishesTenantToUserWithTheOwnerAsTenant()
    {
        _sender.Setup(x => x.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("wamid.human");
        FakeMessagePersistedPublisher publisher = new();
        Guid requestId = Guid.NewGuid();

        ConversationMessage sent = await CreateWithPublisher(publisher).SendAsync(Owner, _connection.Id, Recipient,
            new SendReplyRequest(requestId, "We open at 9"), default);

        MessagePersisted published = Assert.Single(publisher.Published);
        Assert.Equal(CommsType.TenantToUser, published.Data.CommsType);
        Assert.Equal(ConversationService.RequestKey(Owner, requestId), published.Data.MessageId);
        Assert.Equal(sent.Id, published.Data.MessageId);
        Assert.Equal("root-account-1", published.Data.TenantId);
        Assert.Equal(Owner, published.Data.WorkspaceId);
        Assert.Equal("We open at 9", published.Data.Text);
        Assert.True(published.Data.Outgoing);
    }

    [Fact]
    public async Task SendAsync_WhenTheChannelRefusesTheReply_PublishesNothing()
    {
        _sender.Setup(x => x.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new WhatsAppRejectedException());
        FakeMessagePersistedPublisher publisher = new();

        ConversationMessage sent = await CreateWithPublisher(publisher).SendAsync(Owner, _connection.Id, Recipient,
            new SendReplyRequest(Guid.NewGuid(), "We open at 9"), default);

        Assert.Equal("failed", sent.Status);
        Assert.Empty(publisher.Published);
    }

    [Fact]
    public async Task SendAsync_WhenPublishingFails_StillReturnsTheSentReply()
    {
        _sender.Setup(x => x.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("wamid.human");

        ConversationMessage sent = await CreateWithPublisher(new FailingPublisher()).SendAsync(Owner, _connection.Id, Recipient,
            new SendReplyRequest(Guid.NewGuid(), "We open at 9"), default);

        Assert.Equal("accepted", sent.Status);
    }

    [Fact]
    public async Task SendFileAsync_FromATeammate_PublishesTenantToUser()
    {
        _sender.Setup(x => x.SendMediaAsync(It.IsAny<string>(), It.IsAny<string>(), Recipient, "image", "image/png", "menu.png",
            It.IsAny<Stream>(), "Today's menu", It.IsAny<CancellationToken>())).ReturnsAsync("wamid.file");
        FakeMessagePersistedPublisher publisher = new();

        ConversationMessage sent = await CreateWithPublisher(publisher).SendFileAsync(Owner, _connection.Id, Recipient,
            Guid.NewGuid(), "Today's menu", File("menu.png", Png), default);

        MessagePersisted published = Assert.Single(publisher.Published);
        Assert.Equal(CommsType.TenantToUser, published.Data.CommsType);
        Assert.Equal(sent.Id, published.Data.MessageId);
        Assert.Equal("image", Assert.Single(published.Data.Attachments!).Kind);
    }

    [Fact]
    public async Task SendAgentFileAsync_PublishesOnlyAsTheAgent()
    {
        _sender.Setup(x => x.SendMediaAsync(It.IsAny<string>(), It.IsAny<string>(), Recipient, "image", "image/png", "menu.png",
            It.IsAny<Stream>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("wamid.ai-file");
        FakeMessagePersistedPublisher publisher = new();

        await CreateWithPublisher(publisher).SendAgentFileAsync(Owner, _connection.Id, Recipient,
            Guid.NewGuid(), null, File("menu.png", Png), default);

        Assert.Equal(CommsType.AgentToUser, Assert.Single(publisher.Published).Data.CommsType);
    }

    [Fact]
    public async Task SendAgentFileAsync_PublishesThePhotoWithADownloadLink()
    {
        _sender.Setup(x => x.SendMediaAsync(It.IsAny<string>(), It.IsAny<string>(), Recipient, "image", "image/png", "menu.png",
            It.IsAny<Stream>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("wamid.ai-file");
        FakeMessagePersistedPublisher publisher = new();
        Guid requestId = Guid.NewGuid();

        await CreateWithPublisher(publisher, new FakeMediaLinkSigner()).SendAgentFileAsync(Owner, _connection.Id, Recipient,
            requestId, null, File("menu.png", Png), default);

        StoredAttachment photo = Assert.Single(Assert.Single(publisher.Published).Data.Attachments!);
        Assert.Equal("menu.png", photo.FileName);
        Assert.Equal(FakeMediaLinkSigner.LinkFor($"business-media/{Owner}/{requestId:N}.png", "menu.png"), photo.Url);
    }
}
