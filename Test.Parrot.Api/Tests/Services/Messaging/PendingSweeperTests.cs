using Microsoft.Extensions.Logging.Abstractions;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Messaging;
using Parrot.Domain.Entities.Messages;

namespace Test.Parrot.Api.Tests.Services.Messaging;

public class PendingSweeperTests
{
    private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Connection = Guid.Parse("6f1c2d3e-4a5b-4c6d-8e7f-0a1b2c3d4e5f");

    private sealed class Store(params StaleMessage[] stale) : IStoredMessageStore
    {
        public List<(string Id, int Index, IncomingMedia Outcome)> Completed { get; } = new();
        public List<string> FailedSends { get; } = new();
        public (DateTime StoredBefore, DateTime ClaimedBefore)? SendQuery { get; private set; }
        public DateTime? MediaQuery { get; private set; }
        public bool TakenByWorker { get; set; }

        public Task<IReadOnlyList<StaleMessage>> FindStalePendingMediaAsync(DateTime storedBefore, int limit, CancellationToken cancellationToken = default)
        {
            MediaQuery = storedBefore;
            return Task.FromResult<IReadOnlyList<StaleMessage>>(stale);
        }

        public Task<IReadOnlyList<StaleMessage>> FindStalePendingSendsAsync(DateTime storedBefore, DateTime claimedBefore, int limit, CancellationToken cancellationToken = default)
        {
            SendQuery = (storedBefore, claimedBefore);
            return Task.FromResult<IReadOnlyList<StaleMessage>>(stale);
        }

        public Task<bool> CompleteMediaAsync(string messageId, int index, IncomingMedia outcome, CancellationToken cancellationToken = default)
        {
            Completed.Add((messageId, index, outcome));
            return Task.FromResult(true);
        }

        public Task<bool> FailPendingSendAsync(string messageId, CancellationToken cancellationToken = default)
        {
            if (TakenByWorker) return Task.FromResult(false);
            FailedSends.Add(messageId);
            return Task.FromResult(true);
        }

        public Task<MessageDocument?> FindAsync(string messageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<MessageDocument> InsertOrGetAsync(MessageDocument document, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> ClaimSendAsync(string messageId, DateTime claimedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> CompleteSendAsync(string messageId, string status, string? providerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class MediaJobs : IMediaJobPublisher
    {
        public List<MediaJob> Published { get; } = new();

        public Task PublishAsync(MediaJob job, CancellationToken cancellationToken = default)
        {
            Published.Add(job);
            return Task.CompletedTask;
        }
    }

    private sealed class OutboundJobs : IOutboundJobPublisher
    {
        public List<OutboundJob> Published { get; } = new();

        public Task PublishAsync(OutboundJob job, CancellationToken cancellationToken = default)
        {
            Published.Add(job);
            return Task.CompletedTask;
        }
    }

    private sealed class Settled : IMessageSettledPublisher
    {
        public List<MessageSettled> Published { get; } = new();

        public Task PublishAsync(MessageSettled settled, CancellationToken cancellationToken = default)
        {
            Published.Add(settled);
            return Task.CompletedTask;
        }
    }

    private static StaleMessage Incoming(TimeSpan age, params int[] pending) =>
        new("whatsapp:wamid.IMG", "user-1", Connection, "2348012345678", Now - age, "received", pending);

    private static StaleMessage Reply(TimeSpan age, string status = "pending") =>
        new("outgoing:user-1:1", "user-1", Connection, "2348012345678", Now - age, status, []);

    private static PendingSweeper Sweeper(Store store, MediaJobs? media = null, OutboundJobs? outbound = null, Settled? settled = null) =>
        new(store, media ?? new MediaJobs(), outbound ?? new OutboundJobs(), NullLogger<PendingSweeper>.Instance, settled);

    [Fact]
    public async Task A_file_left_pending_past_the_stale_time_has_its_job_queued_again_for_each_pending_position()
    {
        Store store = new(Incoming(TimeSpan.FromMinutes(20), 0, 2));
        MediaJobs jobs = new();

        int acted = await Sweeper(store, jobs).SweepMediaAsync(Now, default);

        Assert.Equal(2, acted);
        Assert.Equal([0, 2], jobs.Published.Select(j => j.Index));
        Assert.All(jobs.Published, j => Assert.Equal("whatsapp:wamid.IMG", j.MessageId));
        Assert.Equal(Now - TimeSpan.FromMinutes(5), store.MediaQuery);
        Assert.Empty(store.Completed);
    }

    [Fact]
    public async Task A_file_pending_for_hours_is_given_up_as_expired_and_the_inbox_told_instead_of_being_queued_again()
    {
        Store store = new(Incoming(TimeSpan.FromHours(7), 1));
        MediaJobs jobs = new();
        Settled settled = new();

        await Sweeper(store, jobs, settled: settled).SweepMediaAsync(Now, default);

        Assert.Empty(jobs.Published);
        (string id, int index, IncomingMedia outcome) = Assert.Single(store.Completed);
        Assert.Equal(("whatsapp:wamid.IMG", 1, MediaStatus.Failed, MediaFailure.Expired), (id, index, outcome.Status, outcome.FailureReason));
        MessageSettled announced = Assert.Single(settled.Published);
        Assert.Equal((SettledKinds.Media, "failed", "2348012345678"), (announced.Kind, announced.Status, announced.Customer));
    }

    [Fact]
    public async Task Nothing_found_means_nothing_is_done()
    {
        MediaJobs media = new();
        OutboundJobs outbound = new();
        PendingSweeper sweeper = Sweeper(new Store(), media, outbound);

        Assert.Equal(0, await sweeper.SweepMediaAsync(Now, default));
        Assert.Equal(0, await sweeper.SweepSendsAsync(Now, default));
        Assert.Empty(media.Published);
        Assert.Empty(outbound.Published);
    }

    [Fact]
    public async Task A_reply_pending_a_little_too_long_is_queued_again_keyed_by_its_thread()
    {
        Store store = new(Reply(TimeSpan.FromMinutes(5)));
        OutboundJobs jobs = new();

        await Sweeper(store, outbound: jobs).SweepSendsAsync(Now, default);

        OutboundJob job = Assert.Single(jobs.Published);
        Assert.Equal(("outgoing:user-1:1", OutboundJob.KeyFor(Connection, "2348012345678")), (job.MessageId, job.ThreadKey));
        Assert.Equal((Now - TimeSpan.FromMinutes(2), Now - OutboundFileProcessor.Lease), store.SendQuery);
        Assert.Empty(store.FailedSends);
    }

    [Fact]
    public async Task A_reply_pending_so_long_that_sending_it_now_would_be_wrong_is_failed_not_sent()
    {
        Store store = new(Reply(TimeSpan.FromHours(2)));
        OutboundJobs jobs = new();
        Settled settled = new();

        await Sweeper(store, outbound: jobs, settled: settled).SweepSendsAsync(Now, default);

        Assert.Equal(["outgoing:user-1:1"], store.FailedSends);
        Assert.Empty(jobs.Published);
        Assert.Equal((SettledKinds.Send, "failed"), (settled.Published.Single().Kind, settled.Published.Single().Status));
    }

    [Fact]
    public async Task A_reply_a_worker_took_while_the_sweeper_looked_is_not_failed_or_announced()
    {
        Store store = new(Reply(TimeSpan.FromHours(2))) { TakenByWorker = true };
        Settled settled = new();

        await Sweeper(store, settled: settled).SweepSendsAsync(Now, default);

        Assert.Empty(store.FailedSends);
        Assert.Empty(settled.Published);
    }

    [Fact]
    public async Task A_reply_sending_past_the_lease_is_queued_so_its_job_can_record_it_unknown_and_it_is_never_failed_as_expired()
    {
        Store store = new(Reply(TimeSpan.FromHours(2), "sending"));
        OutboundJobs jobs = new();

        await Sweeper(store, outbound: jobs).SweepSendsAsync(Now, default);

        Assert.Single(jobs.Published);
        Assert.Empty(store.FailedSends);
    }
}
