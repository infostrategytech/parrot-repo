using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Parrot.Application.Conversations;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Messaging;
using Parrot.Domain.Entities.Messages;
using Parrot.Domain.Enums;

namespace Test.Parrot.Api.Tests.Services.Messaging;

public class OutboundFileProcessorTests
{
    private static readonly Guid Connection = Guid.Parse("6f1c2d3e-4a5b-4c6d-8e7f-0a1b2c3d4e5f");

    // The in-memory twin of the store's two compare-and-sets.
    private sealed class Store(MessageDocument? reply) : IStoredMessageStore
    {
        public int Claims { get; private set; }

        public Task<MessageDocument?> FindAsync(string messageId, CancellationToken cancellationToken = default) =>
            Task.FromResult(reply is not null && reply.MessageId == messageId ? reply : null);

        public Task<MessageDocument> InsertOrGetAsync(MessageDocument d, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> CompleteMediaAsync(string messageId, int index, IncomingMedia outcome, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> ClaimSendAsync(string messageId, DateTime claimedAt, CancellationToken cancellationToken = default)
        {
            if (reply!.SendStatus != "pending") return Task.FromResult(false);
            reply.SendStatus = "sending";
            reply.SendClaimedAt = claimedAt;
            Claims++;
            return Task.FromResult(true);
        }

        public Task<bool> CompleteSendAsync(string messageId, string status, string? providerId, CancellationToken cancellationToken = default)
        {
            if (reply!.SendStatus != "sending") return Task.FromResult(false);
            reply.SendStatus = status;
            reply.ProviderMessageId = providerId;
            return Task.FromResult(true);
        }
        public Task<IReadOnlyList<StaleMessage>> FindStalePendingMediaAsync(DateTime storedBefore, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<StaleMessage>> FindStalePendingSendsAsync(DateTime storedBefore, DateTime claimedBefore, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> FailPendingSendAsync(string messageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    // Answers with each result in turn, then repeats the last; a result that is an exception is thrown.
    private sealed class Dispatcher(params object[] outcomes) : IOutboundFileDispatcher
    {
        public int Calls { get; private set; }

        public Task<FileDispatchResult> DispatchAsync(MessageDocument reply, CancellationToken cancellationToken)
        {
            object outcome = outcomes[Math.Min(++Calls, outcomes.Length) - 1];
            if (outcome is Exception ex) throw ex;
            return Task.FromResult((FileDispatchResult)outcome);
        }
    }

    private sealed class Announcements : IMessageSettledPublisher
    {
        private readonly List<MessageSettled> _published = new();

        // Jobs run at once in the storm tests, so adding is locked.
        public List<MessageSettled> Published { get { lock (_published) return [.. _published]; } }

        public Task PublishAsync(MessageSettled settled, CancellationToken cancellationToken = default)
        {
            lock (_published) _published.Add(settled);
            return Task.CompletedTask;
        }
    }

    private static MessageDocument Reply(string status = "pending", DateTime? claimedAt = null) => new WhatsAppMessageDocument
    {
        MessageId = "outgoing:user-1:0f1c", OwnerUserId = "user-1", ConnectionId = Connection, Platform = MessagingPlatform.WhatsApp,
        ExternalAccountId = "waba", ChannelId = "phone", ChannelDisplayName = "", From = "2348012345678", Timestamp = "1", Type = "image",
        Outgoing = true, SendStatus = status, SendClaimedAt = claimedAt,
        Attachments = [new MessageAttachment { Kind = "image", Status = MediaStatus.Ready, StorageKey = "business-media/user-1/0f1c.png", FileName = "a.png" }],
    };

    private static OutboundJob Job() => new() { MessageId = "outgoing:user-1:0f1c", ThreadKey = OutboundJob.KeyFor(Connection, "2348012345678"), EnqueuedAt = DateTime.UtcNow };

    private static FileDispatchResult Accepted => new("wamid.file", null);

    private static FileDispatchResult Refused(string code) => new(null, code);

    private static OutboundFileProcessor Processor(IStoredMessageStore store, IOutboundFileDispatcher dispatcher, Announcements? announce = null,
        List<TimeSpan>? waits = null, IDeliveryReceipts? receipts = null) =>
        new(store, dispatcher, NullLogger<OutboundFileProcessor>.Instance, announce, receipts, (wait, _) => { waits?.Add(wait); return Task.CompletedTask; }, jitter: () => 0.5);

    [Fact]
    public async Task A_pending_reply_is_claimed_sent_and_recorded_accepted_with_its_channel_id_and_announced()
    {
        MessageDocument reply = Reply();
        Dispatcher dispatcher = new(Accepted);
        Announcements announce = new();

        await Processor(new Store(reply), dispatcher, announce).ProcessAsync(Job(), default);

        Assert.Equal("accepted", reply.SendStatus);
        Assert.Equal("wamid.file", reply.ProviderMessageId);
        Assert.Equal(1, dispatcher.Calls);
        MessageSettled settled = Assert.Single(announce.Published);
        Assert.Equal((SettledKinds.Send, "user-1", Connection, "2348012345678", "accepted"), (settled.Kind, settled.OwnerUserId, settled.ConnectionId, settled.Customer, settled.Status));
    }

    [Fact]
    public async Task The_same_job_delivered_twice_reaches_the_channel_once()
    {
        MessageDocument reply = Reply();
        Dispatcher dispatcher = new(Accepted);
        OutboundFileProcessor processor = Processor(new Store(reply), dispatcher);

        await processor.ProcessAsync(Job(), default);
        await processor.ProcessAsync(Job(), default);

        Assert.Equal(1, dispatcher.Calls);
        Assert.Equal("accepted", reply.SendStatus);
    }

    [Fact]
    public async Task A_reply_already_claimed_by_another_worker_is_not_sent_by_this_one()
    {
        MessageDocument reply = Reply();
        Dispatcher dispatcher = new(Accepted);
        Mock<IStoredMessageStore> store = new();
        store.Setup(s => s.FindAsync(reply.MessageId, It.IsAny<CancellationToken>())).ReturnsAsync(reply);
        store.Setup(s => s.ClaimSendAsync(reply.MessageId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await Processor(store.Object, dispatcher).ProcessAsync(Job(), default);

        Assert.Equal(0, dispatcher.Calls);
        store.Verify(s => s.CompleteSendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("accepted")]
    [InlineData("unknown")]
    public async Task A_reply_that_is_already_settled_is_left_alone(string status)
    {
        MessageDocument reply = Reply(status);
        Dispatcher dispatcher = new(Accepted);

        await Processor(new Store(reply), dispatcher).ProcessAsync(Job(), default);

        Assert.Equal(0, dispatcher.Calls);
        Assert.Equal(status, reply.SendStatus);
    }

    [Fact]
    public async Task A_refusal_from_the_channel_is_recorded_failed_and_not_tried_again()
    {
        MessageDocument reply = Reply();
        Dispatcher dispatcher = new(Refused("131047"));

        await Processor(new Store(reply), dispatcher).ProcessAsync(Job(), default);

        Assert.Equal("failed", reply.SendStatus);
        Assert.Equal(1, dispatcher.Calls);
    }

    [Fact]
    public async Task A_failed_upload_to_the_channel_is_tried_again_because_nothing_was_sent_and_then_succeeds()
    {
        MessageDocument reply = Reply();
        Dispatcher dispatcher = new(Refused("upload"), Refused("upload"), Accepted);
        List<TimeSpan> waits = new();

        await Processor(new Store(reply), dispatcher, waits: waits).ProcessAsync(Job(), default);

        Assert.Equal(3, dispatcher.Calls);
        Assert.Equal(OutboundFileProcessor.RetryDelays, waits);
        Assert.Equal("accepted", reply.SendStatus);
    }

    [Fact]
    public async Task An_upload_that_keeps_failing_ends_failed_after_three_tries()
    {
        MessageDocument reply = Reply();
        Dispatcher dispatcher = new(Refused("upload"));

        await Processor(new Store(reply), dispatcher).ProcessAsync(Job(), default);

        Assert.Equal(3, dispatcher.Calls);
        Assert.Equal("failed", reply.SendStatus);
    }

    [Fact]
    public async Task A_dropped_call_to_the_channel_is_recorded_unknown_and_never_sent_again()
    {
        MessageDocument reply = Reply();
        Dispatcher dispatcher = new(new HttpRequestException("connection reset"), Accepted);
        OutboundFileProcessor processor = Processor(new Store(reply), dispatcher);

        await processor.ProcessAsync(Job(), default);
        await processor.ProcessAsync(Job(), default);

        Assert.Equal("unknown", reply.SendStatus);
        Assert.Equal(1, dispatcher.Calls);
    }

    [Fact]
    public async Task A_call_that_runs_out_of_time_is_recorded_unknown()
    {
        MessageDocument reply = Reply();

        await Processor(new Store(reply), new Dispatcher(new OperationCanceledException())).ProcessAsync(Job(), default);

        Assert.Equal("unknown", reply.SendStatus);
    }

    [Fact]
    public async Task A_worker_that_is_stopped_mid_send_leaves_the_reply_sending_rather_than_guessing()
    {
        MessageDocument reply = Reply();
        using CancellationTokenSource stopping = new();
        Dispatcher dispatcher = new(new OperationCanceledException());
        stopping.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Processor(new Store(reply), dispatcher).ProcessAsync(Job(), stopping.Token));

        Assert.Equal("sending", reply.SendStatus);
    }

    [Fact]
    public async Task A_reply_found_sending_inside_the_lease_is_left_to_the_worker_that_has_it_and_retried_later()
    {
        MessageDocument reply = Reply("sending", DateTime.UtcNow.AddSeconds(-30));
        Dispatcher dispatcher = new(Accepted);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Processor(new Store(reply), dispatcher).ProcessAsync(Job(), default));

        Assert.Equal(0, dispatcher.Calls);
        Assert.Equal("sending", reply.SendStatus);
    }

    [Fact]
    public async Task A_reply_found_sending_past_the_lease_died_mid_send_so_it_is_unknown_and_never_sent_again()
    {
        MessageDocument reply = Reply("sending", DateTime.UtcNow - OutboundFileProcessor.Lease - TimeSpan.FromSeconds(5));
        Dispatcher dispatcher = new(Accepted);
        Announcements announce = new();

        await Processor(new Store(reply), dispatcher, announce).ProcessAsync(Job(), default);

        Assert.Equal("unknown", reply.SendStatus);
        Assert.Equal(0, dispatcher.Calls);
        Assert.Equal("unknown", Assert.Single(announce.Published).Status);
    }

    [Fact]
    public async Task A_job_for_a_reply_that_is_gone_or_is_not_a_file_is_dead_lettered()
    {
        UnprocessableMessageException missing = await Assert.ThrowsAsync<UnprocessableMessageException>(
            () => Processor(new Store(null), new Dispatcher(Accepted)).ProcessAsync(Job(), default));
        Assert.Equal("outbound_message_not_found", missing.Reason);

        MessageDocument text = Reply();
        text.Attachments = null;
        UnprocessableMessageException notAFile = await Assert.ThrowsAsync<UnprocessableMessageException>(
            () => Processor(new Store(text), new Dispatcher(Accepted)).ProcessAsync(Job(), default));
        Assert.Equal("outbound_not_a_file", notAFile.Reason);

        MessageDocument incoming = Reply();
        incoming.Outgoing = false;
        await Assert.ThrowsAsync<UnprocessableMessageException>(
            () => Processor(new Store(incoming), new Dispatcher(Accepted)).ProcessAsync(Job(), default));
    }

    [Fact]
    public async Task A_receipt_that_beat_the_channel_id_is_applied_once_the_id_is_stored()
    {
        MessageDocument reply = Reply();
        Mock<IDeliveryReceipts> receipts = new();

        await Processor(new Store(reply), new Dispatcher(Accepted), receipts: receipts.Object).ProcessAsync(Job(), default);

        receipts.Verify(r => r.ApplyParkedAsync(Connection, "wamid.file", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void A_reply_being_sent_reads_as_pending_in_the_inbox()
    {
        Assert.Equal("pending", ConversationService.DisplayStatus(Reply("sending")));
        Assert.Equal("pending", ConversationService.DisplayStatus(Reply("pending")));
        Assert.Equal("failed", ConversationService.DisplayStatus(Reply("failed")));
    }

    // ---- Storm: many replies, every job delivered several times at once, with failures injected ----

    // Thread-safe, many replies: each compare-and-set is atomic, as MongoDB's single-document updates are.
    private sealed class Shelf(IEnumerable<MessageDocument> replies) : IStoredMessageStore
    {
        private readonly Dictionary<string, MessageDocument> _all = replies.ToDictionary(r => r.MessageId);
        private readonly object _gate = new();

        public MessageDocument Get(string id) { lock (_gate) return _all[id]; }

        public Task<MessageDocument?> FindAsync(string messageId, CancellationToken cancellationToken = default)
        {
            lock (_gate) return Task.FromResult<MessageDocument?>(_all.GetValueOrDefault(messageId));
        }

        public Task<bool> ClaimSendAsync(string messageId, DateTime claimedAt, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                MessageDocument reply = _all[messageId];
                if (reply.SendStatus != "pending") return Task.FromResult(false);
                reply.SendStatus = "sending";
                reply.SendClaimedAt = claimedAt;
                return Task.FromResult(true);
            }
        }

        public Task<bool> CompleteSendAsync(string messageId, string status, string? providerId, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                MessageDocument reply = _all[messageId];
                if (reply.SendStatus != "sending") return Task.FromResult(false);
                reply.SendStatus = status;
                reply.ProviderMessageId = providerId;
                return Task.FromResult(true);
            }
        }

        public Task<MessageDocument> InsertOrGetAsync(MessageDocument d, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> CompleteMediaAsync(string messageId, int index, IncomingMedia outcome, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<StaleMessage>> FindStalePendingMediaAsync(DateTime storedBefore, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<StaleMessage>> FindStalePendingSendsAsync(DateTime storedBefore, DateTime claimedBefore, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> FailPendingSendAsync(string messageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Chaos : IOutboundFileDispatcher
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _calls = new();

        public int CallsFor(string id) => _calls.GetValueOrDefault(id);

        // What this reply's channel does is fixed by its number, so the expected outcome is known.
        public static string Expected(int n) => (n % 4) switch { 0 => "accepted", 1 => "failed", 2 => "unknown", _ => "accepted" };

        public async Task<FileDispatchResult> DispatchAsync(MessageDocument reply, CancellationToken cancellationToken)
        {
            _calls.AddOrUpdate(reply.MessageId, 1, (_, n) => n + 1);
            await Task.Yield();
            int n = int.Parse(reply.MessageId.Split(':')[^1]);
            return (n % 4) switch
            {
                0 or 3 => new FileDispatchResult("wamid." + n, null),
                1 => Refused("131047"),
                _ => throw new HttpRequestException("reset"),
            };
        }
    }

    [Fact]
    public async Task Of_200_replies_each_delivered_as_five_simultaneous_jobs_with_failures_every_reply_reaches_the_channel_exactly_once()
    {
        const int replies = 200, duplicates = 5;
        Shelf shelf = new(Enumerable.Range(0, replies).Select(n =>
        {
            MessageDocument r = Reply();
            r.MessageId = $"outgoing:user-1:{n}";
            return r;
        }));
        Chaos channel = new();
        Announcements announce = new();
        OutboundFileProcessor processor = Processor(shelf, channel, announce);

        await Task.WhenAll(Enumerable.Range(0, replies * duplicates).Select(async k =>
        {
            int n = k % replies;
            OutboundJob job = new() { MessageId = $"outgoing:user-1:{n}", ThreadKey = "t", EnqueuedAt = DateTime.UtcNow };
            try { await processor.ProcessAsync(job, default); }
            catch (InvalidOperationException) { /* "sending" inside its lease: the bus retries it later */ }
        }));

        for (int n = 0; n < replies; n++)
        {
            string id = $"outgoing:user-1:{n}";
            Assert.Equal(1, channel.CallsFor(id));
            Assert.Equal(Chaos.Expected(n), shelf.Get(id).SendStatus);
        }

        // One announcement per reply, however many jobs raced.
        Assert.Equal(replies, announce.Published.Count);
        Assert.Equal(replies, announce.Published.Select(a => a.MessageId).Distinct().Count());
    }
}
