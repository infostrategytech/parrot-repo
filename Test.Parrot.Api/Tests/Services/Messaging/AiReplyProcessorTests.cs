using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Parrot.Application.Agents;
using Parrot.Application.Conversations;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;
using Parrot.Application.Storage;
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

    // A scope factory backed by a single pre-built ConversationService (and, for photos, a downloader).
    private sealed class ScopeFactory(ConversationService service, IAgentPhotoFetcher? photos = null) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(service, photos);

        private sealed class Scope(ConversationService service, IAgentPhotoFetcher? photos) : IServiceScope, IAsyncDisposable
        {
            public IServiceProvider ServiceProvider { get; } = new Provider(service, photos);
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private sealed class Provider(ConversationService service, IAgentPhotoFetcher? photos) : IServiceProvider
        {
            public object? GetService(Type serviceType) =>
                serviceType == typeof(ConversationService) ? service
                : serviceType == typeof(IAgentPhotoFetcher) ? photos
                : null;
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
    private static ConversationService ServiceThatSucceeds() => WhatsAppService(out _, out _, out _);

    // A WhatsApp conversation whose sends are recorded: text, pictures, the stored copies.
    private static ConversationService WhatsAppService(out Mock<IWhatsAppSender> sender, out FakeStore store, out MemoryStorage storage)
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
        sender = new();
        sender.Setup(x => x.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("wamid.reply");
        sender.Setup(x => x.SendMediaAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("wamid.photo");
        Mock<IFacebookConnectionRepository> pages = new();
        pages.Setup(x => x.GetByOwnerAsync(ValidWorkspace, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotFoundException("no page"));
        Mock<IFacebookSender> facebookSender = new();
        Mock<IInstagramConnectionRepository> instagramAccounts = new();
        instagramAccounts.Setup(x => x.GetByOwnerAsync(ValidWorkspace, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotFoundException("no ig"));
        Mock<IInstagramSender> instagramSender = new();

        // Seed an incoming message so PrepareAsync finds a recent one (SendWhatsAppAsync path).
        store = new();
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

        storage = new();
        return new ConversationService(
            store, connections.Object, sender.Object, protector,
            new FakeSequence(), pages.Object, facebookSender.Object, new FakePageProtector(),
            instagramAccounts.Object, instagramSender.Object, new FakeAccountProtector(), mediaStorage: storage);
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

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4];

    private static AiReplyRequested WithPhotos(params (string Url, string? Caption)[] photos) => new()
    {
        WorkspaceId = ValidWorkspace, ConnectionId = ValidConnection,
        Recipient = ValidRecipient, RequestId = ValidRequest, Text = ValidText,
        Attachments = [.. photos.Select(p => new AiReplyAttachment(p.Url, p.Caption))],
    };

    [Fact]
    public async Task Photos_go_after_the_text_in_order_as_the_ais_pictures_and_a_resend_sends_none_again()
    {
        ConversationService service = WhatsAppService(out Mock<IWhatsAppSender> sender, out FakeStore store, out MemoryStorage storage);
        FakePhotos photos = new();
        AiReplyProcessor processor = MakeProcessor(new ScopeFactory(service, photos));
        AiReplyRequested request = WithPhotos(("https://cdn.example/a.jpg", "Adire scarf"), ("https://cdn.example/b.jpg", ""), ("https://cdn.example/a.jpg", "again"));

        await processor.ProcessAsync(request, default);

        // The text first, then each photo once (the repeated link is dropped), in order.
        List<string> calls = sender.Invocations.Select(i => i.Method.Name).ToList();
        Assert.Equal(["SendAsync", "SendMediaAsync", "SendMediaAsync"], calls);
        Assert.Equal(["https://cdn.example/a.jpg", "https://cdn.example/b.jpg"], photos.Fetched);
        object?[] first = sender.Invocations[1].Arguments.ToArray();
        Assert.Equal(("image", "image/jpeg", "Adire scarf"), ((string)first[3]!, (string)first[4]!, (string?)first[7]));
        Assert.Null(sender.Invocations[2].Arguments[7]);

        MessageDocument photo = store.Sent[ConversationService.RequestKey(ValidWorkspace, AiReplyProcessor.PhotoRequestId(ValidRequest, 0))];
        Assert.Equal(("image", true, true, "accepted"), (photo.Type, photo.Outgoing, photo.IsAiReply == true, photo.SendStatus));
        Assert.Equal("Adire scarf", photo.TextBody);
        Assert.True(storage.Has(Assert.Single(photo.Attachments!).StorageKey!));
        Assert.Equal("ai", ConversationService.ToMessage(photo).Kind);

        // The same event again: nothing more goes to the customer.
        await processor.ProcessAsync(request, default);
        Assert.Equal(3, sender.Invocations.Count(i => i.Method.Name is "SendAsync" or "SendMediaAsync"));
    }

    [Fact]
    public async Task A_photo_that_cant_be_had_is_skipped_and_the_rest_still_go()
    {
        ConversationService service = WhatsAppService(out Mock<IWhatsAppSender> sender, out _, out _);
        FakePhotos photos = new() { Broken = "https://cdn.example/broken.jpg" };
        AiReplyProcessor processor = MakeProcessor(new ScopeFactory(service, photos));

        await processor.ProcessAsync(WithPhotos(("https://cdn.example/broken.jpg", "x"), ("https://cdn.example/b.jpg", "Bag")), default);

        Assert.Equal(["SendAsync", "SendMediaAsync"], sender.Invocations.Select(i => i.Method.Name));
        Assert.Equal("Bag", sender.Invocations[1].Arguments[7]);
    }

    [Fact]
    public async Task No_photos_go_when_the_text_wasnt_sent()
    {
        ConversationService service = WhatsAppService(out Mock<IWhatsAppSender> sender, out _, out _);
        sender.Setup(x => x.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new WhatsAppRejectedException("131047"));
        FakePhotos photos = new();
        AiReplyProcessor processor = MakeProcessor(new ScopeFactory(service, photos));

        await processor.ProcessAsync(WithPhotos(("https://cdn.example/a.jpg", "Adire scarf")), default);

        Assert.Empty(photos.Fetched);
        Assert.DoesNotContain(sender.Invocations, i => i.Method.Name == "SendMediaAsync");
    }

    [Fact]
    public void Each_photo_has_its_own_request_id_and_the_same_one_every_time()
    {
        Guid first = AiReplyProcessor.PhotoRequestId(ValidRequest, 0);

        Assert.Equal(first, AiReplyProcessor.PhotoRequestId(ValidRequest, 0));
        Assert.NotEqual(first, AiReplyProcessor.PhotoRequestId(ValidRequest, 1));
        Assert.NotEqual(first, AiReplyProcessor.PhotoRequestId(Guid.NewGuid(), 0));
        Assert.NotEqual(ValidRequest, first);
    }

    /// <summary>Photos by link, as JPEGs; the one named Broken can't be had.</summary>
    private sealed class FakePhotos : IAgentPhotoFetcher
    {
        public string? Broken { get; init; }

        public List<string> Fetched { get; } = [];

        public Task<OutgoingFile> FetchAsync(string url, CancellationToken ct)
        {
            Fetched.Add(url);
            if (url == Broken) throw new AgentPhotoRejectedException("The photo host answered 404.");
            return Task.FromResult(new OutgoingFile("photo.jpg", "image/jpeg", Jpeg.Length, () => new MemoryStream(Jpeg)));
        }
    }

    /// <summary>The inbox's own copies of sent files, in memory.</summary>
    private sealed class MemoryStorage : IPrivateObjectStorage
    {
        private readonly Dictionary<string, byte[]> _files = [];

        public bool Has(string key) => _files.ContainsKey(key);

        public async Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            using MemoryStream copy = new();
            await content.CopyToAsync(copy, cancellationToken);
            _files[key] = copy.ToArray();
        }

        public Task<StoredObjectInfo?> StatAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(_files.TryGetValue(key, out byte[]? b) ? new StoredObjectInfo("image/jpeg", b.Length) : null);

        public Task<StoredObject?> GetRangeAsync(string key, long start, long end, CancellationToken cancellationToken = default) =>
            GetAsync(key, cancellationToken);

        public Task<StoredObject?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(_files.TryGetValue(key, out byte[]? b) ? new StoredObject(new MemoryStream(b), "image/jpeg", b.Length) : null);
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

        // What was sent, by its request key, so a resend finds it as the real store would.
        public Dictionary<string, MessageDocument> Sent { get; } = [];

        public Task<MessageDocument?> FindRequestAsync(string owner, Guid request, CancellationToken ct) =>
            Task.FromResult(Sent.GetValueOrDefault(ConversationService.RequestKey(owner, request)));

        public Task<bool> ReserveAsync(MessageDocument message, CancellationToken ct) =>
            Task.FromResult(Sent.TryAdd(message.MessageId, message));

        public Task CompleteAsync(string id, string status, string? providerId, CancellationToken ct)
        {
            if (Sent.TryGetValue(id, out MessageDocument? doc)) doc.SendStatus = status;
            return Task.CompletedTask;
        }

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
