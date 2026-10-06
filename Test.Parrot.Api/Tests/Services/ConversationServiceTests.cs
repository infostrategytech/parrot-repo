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

    public ConversationServiceTests() => _connection.AccessTokenEncrypted = _protector.Protect("connection-token");

    private ConversationService Create()
    {
        _connections.Setup(x => x.GetByOwnerAndIdAsync(Owner, _connection.Id)).ReturnsAsync(_connection);
        return new ConversationService(_store, _connections.Object, _sender.Object,
            _protector, _sequence);
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
        public Task<bool> ReserveAsync(WhatsAppMessageDocument message, CancellationToken ct) => Task.FromResult(Messages.TryAdd(message.MessageId, message));
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
