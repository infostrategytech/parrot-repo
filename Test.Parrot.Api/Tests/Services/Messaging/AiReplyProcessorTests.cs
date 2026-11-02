using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Parrot.Application.Agents;
using Parrot.Application.Conversations;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;
using Parrot.Domain.Entities;
using Parrot.Domain.Entities.Messages;
using Parrot.Domain.Enums;
using Parrot.Domain.Exceptions;
using Parrot.Infrastructure.SocialMediaIntegrations;

namespace Test.Parrot.Api.Tests.Services.Messaging;

public class AiReplyProcessorTests
{
    private static readonly Guid ValidConnection = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid ValidRequest = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private const string ValidWorkspace = "workspace-1";
    private const string ValidRecipient = "2348012345678";
    private const string ValidText = "Hello from the AI";

    // A scope factory backed by a single pre-built ConversationService.
    private sealed class ScopeFactory(ConversationService service) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(service);

        private sealed class Scope(ConversationService service) : IServiceScope, IAsyncDisposable
        {
            public IServiceProvider ServiceProvider { get; } = new Provider(service);
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private sealed class Provider(ConversationService service) : IServiceProvider
        {
            public object? GetService(Type serviceType) =>
                serviceType == typeof(ConversationService) ? service : null;
        }
    }

    // Builds a ConversationService whose connection lookups all throw the given exception.
    private static ConversationService ServiceThatThrows(Exception ex)
    {
        Mock<IWhatsAppConnectionRepository> connections = new();
        connections.Setup(x => x.GetByOwnerAndIdAsync(It.IsAny<string>(), It.IsAny<Guid>())).ThrowsAsync(ex);
        Mock<IWhatsAppSender> sender = new();
        IWhatsAppTokenProtector protector = new WhatsAppTokenProtector(new EphemeralDataProtectionProvider());
        Mock<IFacebookConnectionRepository> pages = new();
        pages.Setup(x => x.GetByOwnerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(ex);
        Mock<IFacebookSender> facebookSender = new();
        Mock<IInstagramConnectionRepository> instagramAccounts = new();
        instagramAccounts.Setup(x => x.GetByOwnerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(ex);
        Mock<IInstagramSender> instagramSender = new();
        return new ConversationService(
            new FakeStore(), connections.Object, sender.Object, protector,
            new FakeSequence(), pages.Object, facebookSender.Object, new FakePageProtector(),
            instagramAccounts.Object, instagramSender.Object, new FakeAccountProtector());
    }

    // Builds a ConversationService that routes to WhatsApp and completes without error.
    private static ConversationService ServiceThatSucceeds()
    {
        WhatsAppConnection connection = new()
        {
            Id = ValidConnection,
            WorkspaceId = ValidWorkspace, Name = "Test",
            VerifyTokenEncrypted = "encrypted",
            PhoneNumberId = "123", WhatsAppBusinessAccountId = "456",
            WebhookVerifiedAt = DateTime.UtcNow,
        };
        IWhatsAppTokenProtector protector = new WhatsAppTokenProtector(new EphemeralDataProtectionProvider());
        connection.AccessTokenEncrypted = protector.Protect("token");

        Mock<IWhatsAppConnectionRepository> connections = new();
        connections.Setup(x => x.GetByOwnerAndIdAsync(ValidWorkspace, ValidConnection)).ReturnsAsync(connection);
        Mock<IWhatsAppSender> sender = new();
        sender.Setup(x => x.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("wamid.reply");
        Mock<IFacebookConnectionRepository> pages = new();
        pages.Setup(x => x.GetByOwnerAsync(ValidWorkspace, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotFoundException("no page"));
        Mock<IFacebookSender> facebookSender = new();
        Mock<IInstagramConnectionRepository> instagramAccounts = new();
        instagramAccounts.Setup(x => x.GetByOwnerAsync(ValidWorkspace, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotFoundException("no ig"));
        Mock<IInstagramSender> instagramSender = new();

        // Seed an incoming message so PrepareAsync finds a recent one (SendWhatsAppAsync path).
        FakeStore store = new();
        store.SetLatestIncoming(new WhatsAppMessageDocument
        {
            MessageId = "whatsapp:incoming-1",
            OwnerUserId = ValidWorkspace,
            Platform = MessagingPlatform.WhatsApp,
            ExternalAccountId = "456",
            ChannelId = "123",
            ChannelDisplayName = "",
            From = ValidRecipient,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
            Type = "text",
            SentAt = DateTime.UtcNow,
            ReceivedAt = DateTime.UtcNow,
            ConversationKey = $"whatsapp:123:{ValidRecipient}",
            Sequence = 1,
            Receiver = ValidRecipient,
            ConnectionId = ValidConnection,
        });

        return new ConversationService(
            store, connections.Object, sender.Object, protector,
            new FakeSequence(), pages.Object, facebookSender.Object, new FakePageProtector(),
            instagramAccounts.Object, instagramSender.Object, new FakeAccountProtector());
    }

    private static AiReplyProcessor MakeProcessor(IServiceScopeFactory factory) =>
        new(factory, NullLogger<AiReplyProcessor>.Instance);

    [Theory]
    [InlineData("", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "2348012345678", "11111111-2222-3333-4444-555555555555", "Hello")]
    [InlineData("workspace-1", "00000000-0000-0000-0000-000000000000", "2348012345678", "11111111-2222-3333-4444-555555555555", "Hello")]
    [InlineData("workspace-1", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "", "11111111-2222-3333-4444-555555555555", "Hello")]
    [InlineData("workspace-1", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "2348012345678", "00000000-0000-0000-0000-000000000000", "Hello")]
    [InlineData("workspace-1", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "2348012345678", "11111111-2222-3333-4444-555555555555", "")]
    public async Task ProcessAsync_WhenFieldMissing_ThrowsUnprocessableWithAiReplyMissingFields(
        string workspaceId, Guid connectionId, string recipient, Guid requestId, string text)
    {
        AiReplyProcessor processor = MakeProcessor(Mock.Of<IServiceScopeFactory>());
        AiReplyRequested request = new()
        {
            WorkspaceId = workspaceId, ConnectionId = connectionId,
            Recipient = recipient, RequestId = requestId, Text = text,
        };

        UnprocessableMessageException ex = await Assert.ThrowsAsync<UnprocessableMessageException>(
            () => processor.ProcessAsync(request, default));

        Assert.Equal("ai_reply_missing_fields", ex.Reason);
    }

    [Fact]
    public async Task ProcessAsync_WhenConnectionNotFound_ThrowsUnprocessableWithConnectionNotFound()
    {
        AiReplyProcessor processor = MakeProcessor(new ScopeFactory(ServiceThatThrows(new NotFoundException("Connection not found."))));
        AiReplyRequested request = new()
        {
            WorkspaceId = ValidWorkspace, ConnectionId = ValidConnection,
            Recipient = ValidRecipient, RequestId = ValidRequest, Text = ValidText,
        };

        UnprocessableMessageException ex = await Assert.ThrowsAsync<UnprocessableMessageException>(
            () => processor.ProcessAsync(request, default));

        Assert.Equal("connection_not_found", ex.Reason);
        Assert.IsType<NotFoundException>(ex.InnerException);
    }

    [Fact]
    public async Task ProcessAsync_WhenValidationFails_ThrowsUnprocessableWithValidationFailed()
    {
        AiReplyProcessor processor = MakeProcessor(new ScopeFactory(ServiceThatThrows(new ValidationException("Text is too long."))));
        AiReplyRequested request = new()
        {
            WorkspaceId = ValidWorkspace, ConnectionId = ValidConnection,
            Recipient = ValidRecipient, RequestId = ValidRequest, Text = ValidText,
        };

        UnprocessableMessageException ex = await Assert.ThrowsAsync<UnprocessableMessageException>(
            () => processor.ProcessAsync(request, default));

        Assert.Equal("validation_failed", ex.Reason);
        Assert.IsType<ValidationException>(ex.InnerException);
    }

    [Fact]
    public async Task ProcessAsync_WhenRequestIsValid_SendsWithoutException()
    {
        AiReplyProcessor processor = MakeProcessor(new ScopeFactory(ServiceThatSucceeds()));
        AiReplyRequested request = new()
        {
            WorkspaceId = ValidWorkspace, ConnectionId = ValidConnection,
            Recipient = ValidRecipient, RequestId = ValidRequest, Text = ValidText,
        };

        // Should route to WhatsApp and complete without throwing.
        await processor.ProcessAsync(request, default);
    }

    // Minimal store: returns the seeded incoming message, reserves and completes without error.
    private sealed class FakeStore : IConversationStore
    {
        private MessageDocument? _latestIncoming;

        public void SetLatestIncoming(MessageDocument doc) => _latestIncoming = doc;

        public Task<IReadOnlyList<ConversationSummary>> ListAsync(string owner, int skip, int take, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ConversationSummary>>([]);

        public Task<IReadOnlyList<ConversationSummary>> FindAsync(string owner,
            IReadOnlyCollection<(Guid Connection, string Sender)> conversations, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ConversationSummary>>([]);

        public Task<MessagePage> ReadAsync(string owner, Guid connection, string sender, string? before, CancellationToken ct) =>
            Task.FromResult(new MessagePage([], null));

        public Task<MediaPage> ListMediaAsync(string owner, Guid connection, string sender, string? before, CancellationToken ct) =>
            Task.FromResult(new MediaPage([], null));

        public Task<MessageDocument?> LatestIncomingAsync(string owner, Guid connection, string sender, CancellationToken ct) =>
            Task.FromResult(_latestIncoming);

        public Task<MessageDocument?> FindRequestAsync(string owner, Guid request, CancellationToken ct) =>
            Task.FromResult<MessageDocument?>(null);

        public Task<bool> ReserveAsync(MessageDocument message, CancellationToken ct) =>
            Task.FromResult(true);

        public Task CompleteAsync(string id, string status, string? providerId, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<bool> FailPendingAsync(string id, CancellationToken ct) =>
            Task.FromResult(false);
    }

    private sealed class FakeSequence : IConversationSequence
    {
        private long _next;
        public Task<long> NextAsync(string conversationKey, CancellationToken ct = default) =>
            Task.FromResult(++_next);
    }

    private sealed class FakePageProtector : IFacebookCredentialProtector
    {
        public string Protect(string v) => $"enc:{v}";
        public string Unprotect(string v) => v.StartsWith("enc:") ? v[4..] : v;
    }

    private sealed class FakeAccountProtector : IInstagramCredentialProtector
    {
        public string Protect(string v) => $"enc:{v}";
        public string Unprotect(string v) => v.StartsWith("enc:") ? v[4..] : v;
    }
}
