using System.Collections.Concurrent;
using Microsoft.AspNetCore.DataProtection;
using Parrot.Infrastructure.SocialMediaIntegrations;
using Moq;
using Parrot.Application.Conversations;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;
using Parrot.Domain.Entities;
using Parrot.Domain.Entities.Messages;
using Parrot.Domain.Enums;
using Parrot.Domain.Exceptions;

namespace Test.Parrot.Api.Tests.Services;

public class ConversationServiceTests
{
    private const string Owner = "owner";
    private const string Recipient = "2348012345678";
    private readonly WhatsAppConnection _connection = new()
    {
        OwnerUserId = Owner, Name = "Test", VerifyTokenEncrypted = "encrypted",
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
        OwnerUserId = Owner, VerifyTokenEncrypted = "encrypted", PageId = PageId, PageName = "Oma's Kitchen",
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
        public Task<IReadOnlyList<ConversationSummary>> ListAsync(string owner, int skip, int take, CancellationToken ct) => throw new NotImplementedException();
        public Task<MessagePage> ReadAsync(string owner, Guid connection, string sender, string? before, CancellationToken ct) => throw new NotImplementedException();
    }
}
