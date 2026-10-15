using Microsoft.Extensions.Logging.Abstractions;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Messaging;
using Parrot.Domain.Entities.Messages;
using Parrot.Domain.Enums;

namespace Test.Parrot.Api.Tests.Services.Messaging;

/// <summary>The in-memory twin of the store's compare-and-set, shared by the fakes.</summary>
internal static class MediaTestStore
{
    public static bool Complete(MessageDocument? document, int index, IncomingMedia outcome)
    {
        MessageAttachment? a = document?.Attachments?.ElementAtOrDefault(index);
        if (a is null || a.Status != MediaStatus.Pending) return false;
        a.Status = outcome.Status; a.FailureReason = outcome.FailureReason; a.StorageKey = outcome.StorageKey;
        a.Kind = outcome.Kind; a.ContentType = outcome.ContentType; a.SizeBytes = outcome.SizeBytes; a.SourceUrl = null;
        return true;
    }
}

public class MediaJobProcessorTests
{
    private sealed class Store(MessageDocument? document) : IStoredMessageStore
    {
        public int Completed { get; private set; }

        public Task<MessageDocument?> FindAsync(string messageId, CancellationToken cancellationToken = default) =>
            Task.FromResult(document is not null && document.MessageId == messageId ? document : null);

        public Task<MessageDocument> InsertOrGetAsync(MessageDocument d, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> CompleteMediaAsync(string messageId, int index, IncomingMedia outcome, CancellationToken cancellationToken = default)
        {
            bool applied = MediaTestStore.Complete(document, index, outcome);
            if (applied) Completed++;
            return Task.FromResult(applied);
        }
    }

    // Answers with each outcome in turn, then repeats the last.
    private sealed class Media(params IncomingMedia[] outcomes) : IInboundMediaStore
    {
        public List<(IncomingMessage Message, int Index)> Calls { get; } = new();

        public Task<IReadOnlyList<IncomingMedia>> SaveAsync(IncomingMessage message, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IncomingMedia> SaveFileAsync(IncomingMessage message, int index, CancellationToken cancellationToken)
        {
            Calls.Add((message, index));
            return Task.FromResult(outcomes[Math.Min(Calls.Count, outcomes.Length) - 1]);
        }
    }

    private static MessageDocument Document(params MessageAttachment[] files) => new WhatsAppMessageDocument
    {
        MessageId = "whatsapp:wamid.IMG", OwnerUserId = "user-1", ConnectionId = Guid.NewGuid(), Platform = MessagingPlatform.WhatsApp,
        ExternalAccountId = "waba-1", ChannelId = "phone-1", ChannelDisplayName = "", From = "2348012345678", Timestamp = "1",
        Type = "image", Attachments = [.. files],
    };

    private static MessageAttachment Pending(string kind = MediaKinds.Image, string? mediaId = "media-1") =>
        new() { Kind = kind, Status = MediaStatus.Pending, ProviderMediaId = mediaId };

    private static IncomingMedia Ready => new() { Kind = MediaKinds.Image, Status = MediaStatus.Ready, StorageKey = "customer-media/user-1/a-0.jpg", ContentType = "image/jpeg", SizeBytes = 3 };

    private static IncomingMedia Failed(string reason) => new() { Kind = MediaKinds.Image, Status = MediaStatus.Failed, FailureReason = reason };

    private static MediaJob Job(int index = 0) => new() { MessageId = "whatsapp:wamid.IMG", Index = index, CorrelationId = "c", EnqueuedAt = DateTime.UtcNow };

    private sealed class Announcements : IMediaSettledPublisher
    {
        public List<MediaSettled> Published { get; } = new();

        public Task PublishAsync(MediaSettled settled, CancellationToken cancellationToken = default)
        {
            Published.Add(settled);
            return Task.CompletedTask;
        }
    }

    private static MediaJobProcessor Processor(IStoredMessageStore store, IInboundMediaStore media, List<TimeSpan>? waits = null, Announcements? announce = null) =>
        new(store, media, NullLogger<MediaJobProcessor>.Instance, announce, (wait, _) => { waits?.Add(wait); return Task.CompletedTask; });

    [Fact]
    public async Task A_settled_file_is_announced_once_to_its_owner_with_the_customer_and_outcome()
    {
        MessageDocument document = Document(Pending());
        document.ConnectionId = Guid.Parse("6f1c2d3e-4a5b-4c6d-8e7f-0a1b2c3d4e5f");
        Announcements announce = new();

        await Processor(new Store(document), new Media(Ready), announce: announce).ProcessAsync(Job(), default);

        MediaSettled settled = Assert.Single(announce.Published);
        Assert.Equal("whatsapp:wamid.IMG", settled.MessageId);
        Assert.Equal("user-1", settled.OwnerUserId);
        Assert.Equal(document.ConnectionId, settled.ConnectionId);
        Assert.Equal("2348012345678", settled.Customer);
        Assert.Equal(MediaStatus.Ready, settled.Status);
    }

    [Fact]
    public async Task A_failure_and_a_too_large_file_are_announced_too_so_the_inbox_stops_saying_loading()
    {
        Announcements announce = new();

        await Processor(new Store(Document(Pending())), new Media(Failed(MediaFailure.Refused)), announce: announce).ProcessAsync(Job(), default);
        await Processor(new Store(Document(Pending())), new Media(new IncomingMedia { Kind = "image", Status = MediaStatus.TooLarge }), announce: announce).ProcessAsync(Job(), default);

        Assert.Equal([MediaStatus.Failed, MediaStatus.TooLarge], announce.Published.Select(p => p.Status));
    }

    [Fact]
    public async Task A_job_that_finds_the_file_already_settled_announces_nothing()
    {
        Announcements announce = new();
        MessageDocument document = Document(new MessageAttachment { Kind = MediaKinds.Image, Status = MediaStatus.Ready, StorageKey = "k" });

        await Processor(new Store(document), new Media(Ready), announce: announce).ProcessAsync(Job(), default);

        Assert.Empty(announce.Published);
    }

    [Fact]
    public async Task A_message_with_no_owner_is_settled_without_an_announcement()
    {
        Announcements announce = new();
        MessageDocument document = Document(Pending());
        document.OwnerUserId = null;

        await Processor(new Store(document), new Media(Ready), announce: announce).ProcessAsync(Job(), default);

        Assert.Empty(announce.Published);
        Assert.Equal(MediaStatus.Ready, document.Attachments![0].Status);
    }

    [Fact]
    public async Task A_pending_file_is_copied_and_the_attachment_becomes_ready_with_its_key()
    {
        MessageDocument document = Document(Pending());
        Media media = new(Ready);

        await Processor(new Store(document), media).ProcessAsync(Job(), default);

        MessageAttachment a = Assert.Single(document.Attachments!);
        Assert.Equal(MediaStatus.Ready, a.Status);
        Assert.Equal("customer-media/user-1/a-0.jpg", a.StorageKey);
        Assert.Equal(3, a.SizeBytes);
        Assert.Single(media.Calls);
    }

    [Fact]
    public async Task Only_the_file_named_by_the_job_is_given_an_address_and_it_keeps_its_position()
    {
        MessageDocument document = Document(Pending(mediaId: "first"), Pending(mediaId: "second"));
        Media media = new(Ready);

        await Processor(new Store(document), media).ProcessAsync(Job(index: 1), default);

        (IncomingMessage message, int index) = Assert.Single(media.Calls);
        Assert.Equal(1, index);
        Assert.Null(message.Media![0].ProviderMediaId);
        Assert.Equal("second", message.Media![1].ProviderMediaId);
        Assert.Equal(MediaStatus.Pending, document.Attachments![0].Status);
        Assert.Equal(MediaStatus.Ready, document.Attachments![1].Status);
    }

    [Fact]
    public async Task A_file_that_is_already_settled_is_not_downloaded_again()
    {
        MessageDocument document = Document(new MessageAttachment { Kind = MediaKinds.Image, Status = MediaStatus.Ready, StorageKey = "k" });
        Media media = new(Ready);

        await Processor(new Store(document), media).ProcessAsync(Job(), default);

        Assert.Empty(media.Calls);
        Assert.Equal("k", document.Attachments![0].StorageKey);
    }

    [Fact]
    public async Task A_busy_platform_is_asked_again_after_a_wait_until_it_answers()
    {
        MessageDocument document = Document(Pending());
        Media media = new(Failed(MediaFailure.Unavailable), Failed(MediaFailure.Timeout), Ready);
        List<TimeSpan> waits = new();

        await Processor(new Store(document), media, waits).ProcessAsync(Job(), default);

        Assert.Equal(3, media.Calls.Count);
        Assert.Equal(MediaJobProcessor.RetryDelays, waits);
        Assert.Equal(MediaStatus.Ready, document.Attachments![0].Status);
    }

    [Fact]
    public async Task A_platform_that_keeps_failing_ends_with_the_file_marked_failed_after_three_tries()
    {
        MessageDocument document = Document(Pending());
        Media media = new(Failed(MediaFailure.Unavailable));

        await Processor(new Store(document), media).ProcessAsync(Job(), default);

        Assert.Equal(3, media.Calls.Count);
        MessageAttachment a = document.Attachments![0];
        Assert.Equal(MediaStatus.Failed, a.Status);
        Assert.Equal(MediaFailure.Unavailable, a.FailureReason);
    }

    [Fact]
    public async Task A_refusal_is_recorded_at_once_without_asking_again()
    {
        MessageDocument document = Document(Pending());
        Media media = new(Failed(MediaFailure.Refused), Ready);

        await Processor(new Store(document), media).ProcessAsync(Job(), default);

        Assert.Single(media.Calls);
        Assert.Equal(MediaFailure.Refused, document.Attachments![0].FailureReason);
    }

    [Fact]
    public async Task A_file_over_the_limit_is_recorded_as_too_large()
    {
        MessageDocument document = Document(Pending(MediaKinds.Video));
        Media media = new(new IncomingMedia { Kind = MediaKinds.Video, Status = MediaStatus.TooLarge, SizeBytes = 99_000_000 });

        await Processor(new Store(document), media).ProcessAsync(Job(), default);

        Assert.Single(media.Calls);
        Assert.Equal(MediaStatus.TooLarge, document.Attachments![0].Status);
        Assert.Equal(99_000_000, document.Attachments![0].SizeBytes);
    }

    [Fact]
    public async Task A_job_for_a_message_that_is_not_there_is_dead_lettered()
    {
        UnprocessableMessageException ex = await Assert.ThrowsAsync<UnprocessableMessageException>(
            () => Processor(new Store(null), new Media(Ready)).ProcessAsync(Job(), default));
        Assert.Equal("media_message_not_found", ex.Reason);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public async Task A_job_for_a_position_the_message_does_not_have_is_dead_lettered(int index)
    {
        UnprocessableMessageException ex = await Assert.ThrowsAsync<UnprocessableMessageException>(
            () => Processor(new Store(Document(Pending())), new Media(Ready)).ProcessAsync(Job(index), default));
        Assert.Equal("media_attachment_not_found", ex.Reason);
    }
}
