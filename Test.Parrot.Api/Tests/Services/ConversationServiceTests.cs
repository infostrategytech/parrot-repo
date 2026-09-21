using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Moq;
using Parrot.Application.Conversations;
using Parrot.Application.Integrations;
using Parrot.Domain.Entities;
using Parrot.Domain.Entities.Messages;
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

    private ConversationService Create()
    {
        _connections.Setup(x => x.GetByOwnerAndIdAsync(Owner, _connection.Id)).ReturnsAsync(_connection);
        return new ConversationService(_store, _connections.Object, _sender.Object,
            Options.Create(new WhatsAppSettings { AccessToken = "test", PhoneNumberId = "123", BusinessAccountId = "456" }));
    }

    [Fact]
    public async Task ConcurrentAndRepeatedRequestsSendOnlyOnce()
    {
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _sender.Setup(x => x.SendAsync("123", Recipient, "Hello", It.IsAny<CancellationToken>())).Returns(release.Task);
        var service = Create();
        var request = new SendReplyRequest(Guid.NewGuid(), "Hello");
        var first = service.SendAsync(Owner, _connection.Id, Recipient, request, default);
        var second = await service.SendAsync(Owner, _connection.Id, Recipient, request, default);
        Assert.Equal("pending", second.Status);
        release.SetResult("wamid.unique");
        Assert.Equal("accepted", (await first).Status);
        Assert.Equal("accepted", (await service.SendAsync(Owner, _connection.Id, Recipient, request, default)).Status);
        _sender.Verify(x => x.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Single(_store.Messages);
    }

    [Fact]
    public async Task TimeoutIsNotAutomaticallyResent()
    {
        _sender.Setup(x => x.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException());
        var service = Create();
        var request = new SendReplyRequest(Guid.NewGuid(), "Hello");
        Assert.Equal("unknown", (await service.SendAsync(Owner, _connection.Id, Recipient, request, default)).Status);
        Assert.Equal("unknown", (await service.SendAsync(Owner, _connection.Id, Recipient, request, default)).Status);
        _sender.Verify(x => x.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PersistenceFailureAfterMetaAcceptanceDoesNotResend()
    {
        _store.FailComplete = true;
        _sender.Setup(x => x.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("wamid.accepted");
        var service = Create();
        var request = new SendReplyRequest(Guid.NewGuid(), "Hello");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendAsync(Owner, _connection.Id, Recipient, request, default));
        Assert.Equal("pending", (await service.SendAsync(Owner, _connection.Id, Recipient, request, default)).Status);
        _sender.Verify(x => x.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SameRequestIdWithDifferentTextIsRejected()
    {
        _sender.Setup(x => x.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("wamid.accepted");
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
        _store.LastIncoming = DateTime.UtcNow.AddHours(-25);
        var service = Create();
        await Assert.ThrowsAsync<ConflictException>(() => service.SendAsync(Owner, _connection.Id, Recipient, new(Guid.NewGuid(), "Hi"), default));
        Assert.Empty(_store.Messages);
    }

    [Fact]
    public async Task CredentialAccountMismatchCannotSend()
    {
        _connection.PhoneNumberId = "different";
        var service = Create();
        await Assert.ThrowsAsync<ConflictException>(() => service.SendAsync(Owner, _connection.Id, Recipient, new(Guid.NewGuid(), "Hi"), default));
        Assert.Empty(_store.Messages);
    }

    [Fact]
    public async Task ExplicitProviderRejectionIsStoredAsFailed()
    {
        _sender.Setup(x => x.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new WhatsAppRejectedException());
        var service = Create();
        Assert.Equal("failed", (await service.SendAsync(Owner, _connection.Id, Recipient, new(Guid.NewGuid(), "Hi"), default)).Status);
    }

    private sealed class MemoryStore : IConversationStore
    {
        public ConcurrentDictionary<string, MessageDocument> Messages { get; } = new();
        public DateTime? LastIncoming { get; set; } = DateTime.UtcNow;
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
        public Task<DateTime?> LastIncomingAsync(string owner, Guid connection, string sender, CancellationToken ct) => Task.FromResult(LastIncoming);
        public Task<IReadOnlyList<ConversationSummary>> ListAsync(string owner, int skip, int take, CancellationToken ct) => throw new NotImplementedException();
        public Task<MessagePage> ReadAsync(string owner, Guid connection, string sender, string? before, CancellationToken ct) => throw new NotImplementedException();
    }
}
