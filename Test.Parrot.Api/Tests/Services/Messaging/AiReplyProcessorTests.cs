using System.Text.Json;
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
    private static ConversationService WhatsAppService(out Mock<IWhatsAppSender> sender, out FakeStore store, out MemoryStorage storage,
        IMessagePersistedPublisher? publisher = null)
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
            instagramAccounts.Object, instagramSender.Object, new FakeAccountProtector(), mediaStorage: storage, publisher: publisher);
    }

    /// <summary>What was announced on messages.persisted.</summary>
    private sealed class RecordingPublisher : IMessagePersistedPublisher
    {
        public List<MessagePersisted> Published { get; } = [];

        public Task PublishAsync(MessagePersisted message, CancellationToken cancellationToken)
        {
            Published.Add(message);
            return Task.CompletedTask;
        }
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

    // A send the channel set-up refuses (disconnected, no token, a request id reused for other text) never succeeds on
    // a retry: it is dead-lettered, not read again for ever while it holds up the other replies in its batch.
    [Fact]
    public async Task ProcessAsync_WhenTheSendIsRefused_ThrowsUnprocessableSoItIsDeadLetteredNotRetried()
    {
        AiReplyProcessor processor = MakeProcessor(new ScopeFactory(ServiceThatThrows(new ConflictException("This connection is disabled or unverified."))));
        AiReplyRequested request = new()
        {
            WorkspaceId = ValidWorkspace, ConnectionId = ValidConnection,
            Recipient = ValidRecipient, RequestId = ValidRequest, Text = ValidText,
        };

        UnprocessableMessageException ex = await Assert.ThrowsAsync<UnprocessableMessageException>(
            () => processor.ProcessAsync(request, default));

        Assert.Equal("send_refused", ex.Reason);
        Assert.IsType<ConflictException>(ex.InnerException);
    }

    // The common case: the agent answers after the customer's 24 hours have passed. WhatsApp would refuse it today and
    // every day after, so it goes to the dead-letter topic, and nothing is stored or sent.
    [Fact]
    public async Task ProcessAsync_WhenTheCustomersReplyWindowHasClosed_ThrowsUnprocessableAndSendsNothing()
    {
        ConversationService service = WhatsAppService(out Mock<IWhatsAppSender> sender, out FakeStore store, out _);
        DateTime lastWrote = DateTime.UtcNow.AddHours(-25);
        store.SetLatestIncoming(new WhatsAppMessageDocument
        {
            MessageId = "whatsapp:incoming-old", OwnerUserId = ValidWorkspace, Platform = MessagingPlatform.WhatsApp,
            ExternalAccountId = "456", ChannelId = "123", ChannelDisplayName = "", From = ValidRecipient,
            Timestamp = new DateTimeOffset(lastWrote).ToUnixTimeSeconds().ToString(), Type = "text",
            SentAt = lastWrote, ReceivedAt = lastWrote, ConversationKey = $"whatsapp:123:{ValidRecipient}", Sequence = 1,
            Receiver = ValidRecipient, ConnectionId = ValidConnection,
        });
        AiReplyRequested request = new()
        {
            WorkspaceId = ValidWorkspace, ConnectionId = ValidConnection,
            Recipient = ValidRecipient, RequestId = ValidRequest, Text = ValidText,
        };

        UnprocessableMessageException ex = await Assert.ThrowsAsync<UnprocessableMessageException>(
            () => MakeProcessor(new ScopeFactory(service)).ProcessAsync(request, default));

        Assert.Equal("send_refused", ex.Reason);
        sender.Verify(x => x.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // A batch read again (one bad reply in it rewinds the lot) hands every reply in it over again. One already sent is
    // not sent again, and must not be announced to Omni again either: that repeat was the endless stream of AgentToUser.
    [Fact]
    public async Task ProcessAsync_WhenTheSameReplyIsDeliveredAgain_AnnouncesItOnlyOnce()
    {
        RecordingPublisher published = new();
        ConversationService service = WhatsAppService(out Mock<IWhatsAppSender> sender, out _, out _, published);
        AiReplyProcessor processor = MakeProcessor(new ScopeFactory(service));
        AiReplyRequested request = new()
        {
            WorkspaceId = ValidWorkspace, ConnectionId = ValidConnection,
            Recipient = ValidRecipient, RequestId = ValidRequest, Text = ValidText,
        };

        await processor.ProcessAsync(request, default);
        await processor.ProcessAsync(request, default);
        await processor.ProcessAsync(request, default);

        MessagePersisted announced = Assert.Single(published.Published);
        Assert.Equal(CommsType.AgentToUser, announced.Data.CommsType);
        Assert.True(announced.Data.Outgoing);
        sender.Verify(x => x.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
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

    // The AI service's super agent names the agents it asked, with what each was asked and answered.
    private const string TwoHelpers = """
        [{"agentId":"agt_01JC","name":"Tracking","avatar":"tr","useCase":"tracking","input":"Where is ORD-1041?",
          "output":"Your parcel ORD-1041 left Ikeja this morning.","status":"answered","latencyMs":812.0},
         {"agentId":"agt_01JB","name":"Sales","avatar":null,"useCase":null,"input":"How much are the blue sneakers?",
          "output":"","status":"timed_out","latencyMs":null}]
        """;

    private static AiReplyRequested WithHelpers(string contributors) => new()
    {
        WorkspaceId = ValidWorkspace, ConnectionId = ValidConnection,
        Recipient = ValidRecipient, RequestId = ValidRequest, Text = ValidText,
        Contributors = JsonDocument.Parse(contributors).RootElement.Clone(),
    };

    [Fact]
    public async Task ProcessAsync_WithTheAgentsThatHelped_KeepsThemWithTheReply()
    {
        ConversationService service = WhatsAppService(out _, out FakeStore store, out _);

        await MakeProcessor(new ScopeFactory(service)).ProcessAsync(WithHelpers(TwoHelpers), default);

        List<ReplyContributor> kept = store.Sent[ConversationService.RequestKey(ValidWorkspace, ValidRequest)].Contributors!;
        Assert.Equal(2, kept.Count);
        Assert.Equal(("agt_01JC", "Tracking", "tr"), (kept[0].AgentId, kept[0].Name, kept[0].Avatar));
        Assert.Equal(("Where is ORD-1041?", "Your parcel ORD-1041 left Ikeja this morning.", "answered"), (kept[0].Question, kept[0].Answer, kept[0].Status));
        Assert.Equal(("Sales", null, "", "timed_out"), (kept[1].Name, kept[1].Avatar, kept[1].Answer, kept[1].Status));
    }

    [Theory]
    [InlineData("\"not a list\"")]
    [InlineData("""[{"agentId":7,"name":["Tracking"],"input":{"a":1},"status":true}]""")]
    [InlineData("""[{"name":"No id"},{"agentId":"agt_1"}]""")]
    public async Task ProcessAsync_WithHelpersItCantRead_StillSendsTheReplyAndKeepsNone(string contributors)
    {
        ConversationService service = WhatsAppService(out Mock<IWhatsAppSender> sender, out FakeStore store, out _);

        await MakeProcessor(new ScopeFactory(service)).ProcessAsync(WithHelpers(contributors), default);

        sender.Verify(x => x.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), ValidText, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Null(store.Sent[ConversationService.RequestKey(ValidWorkspace, ValidRequest)].Contributors);
    }

    [Fact]
    public async Task ProcessAsync_KeepsAtMostThreeHelpersWithTheirWordsCutTo1500Characters()
    {
        ConversationService service = WhatsAppService(out _, out FakeStore store, out _);
        string longWords = new('a', 2000);
        string five = "[" + string.Join(",", Enumerable.Range(1, 5).Select(i =>
            $$"""{"agentId":"agt_{{i}}","name":"Agent {{i}}","input":"{{longWords}}","output":"{{longWords}}","status":"answered"}""")) + "]";

        await MakeProcessor(new ScopeFactory(service)).ProcessAsync(WithHelpers(five), default);

        List<ReplyContributor> kept = store.Sent[ConversationService.RequestKey(ValidWorkspace, ValidRequest)].Contributors!;
        Assert.Equal(["agt_1", "agt_2", "agt_3"], kept.Select(c => c.AgentId));
        Assert.All(kept, c => Assert.Equal((1500, 1500), (c.Question.Length, c.Answer.Length)));
    }

    [Fact]
    public async Task ProcessAsync_WhenTheHelpersCantBeKept_TheReplyStillCountsAsSentAndIsAnnouncedOnce()
    {
        RecordingPublisher published = new();
        ConversationService service = WhatsAppService(out Mock<IWhatsAppSender> sender, out FakeStore store, out _, published);
        store.ContributorsFailure = new IOException("Mongo is down");

        await MakeProcessor(new ScopeFactory(service)).ProcessAsync(WithHelpers(TwoHelpers), default);

        sender.Verify(x => x.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), ValidText, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Single(published.Published);
    }

    [Fact]
    public async Task ProcessAsync_WithoutHelpers_KeepsNoneAsBefore()
    {
        ConversationService service = WhatsAppService(out _, out FakeStore store, out _);

        await MakeProcessor(new ScopeFactory(service)).ProcessAsync(new AiReplyRequested
        {
            WorkspaceId = ValidWorkspace, ConnectionId = ValidConnection, Recipient = ValidRecipient, RequestId = ValidRequest, Text = ValidText,
        }, default);

        Assert.Null(store.Sent[ConversationService.RequestKey(ValidWorkspace, ValidRequest)].Contributors);
        Assert.Equal(0, store.ContributorWrites);
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

        public Exception? ContributorsFailure { get; set; }
        public int ContributorWrites { get; private set; }

        public Task SetContributorsAsync(string owner, Guid request, IReadOnlyList<ReplyContributor> contributors, CancellationToken ct)
        {
            ContributorWrites++;
            if (ContributorsFailure is not null) throw ContributorsFailure;
            if (Sent.TryGetValue(ConversationService.RequestKey(owner, request), out MessageDocument? doc)) doc.Contributors = [.. contributors];
            return Task.CompletedTask;
        }
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
