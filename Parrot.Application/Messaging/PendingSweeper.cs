using Microsoft.Extensions.Logging;
using Parrot.Application.DTOs.Integrations;
using Parrot.Domain.Entities.Messages;

namespace Parrot.Application.Messaging;

/// <summary>How long work may wait before the sweeper acts on it. The defaults leave a healthy pipeline untouched.</summary>
public sealed record SweepTimings
{
    /// <summary>A file still pending this long after its message was stored has lost its job or is stuck: queue it again.</summary>
    public TimeSpan MediaStaleAfter { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>A file still pending this long is given up: the platform's link is long dead. Recorded failed ("expired").</summary>
    public TimeSpan MediaExpireAfter { get; init; } = TimeSpan.FromHours(6);

    /// <summary>A reply still pending this long lost its job: queue it again (the send worker claims before it sends, so this is safe).</summary>
    public TimeSpan SendStaleAfter { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>A reply still pending this long is not sent any more: a message arriving hours late is worse than a failed one.</summary>
    public TimeSpan SendExpireAfter { get; init; } = TimeSpan.FromMinutes(30);

    public int BatchSize { get; init; } = 100;
}

/// <summary>
/// Finds work the pipeline lost or left waiting, because MongoDB and Kafka do not share a transaction: a message can be
/// stored and its media job never queued, or a job can be dropped, or a worker can die with a file or reply in hand.
/// Everything it does is safe to do twice: a requeued job is skipped by the worker if the work is already settled,
/// and a reply is claimed by exactly one worker before it is sent.
///
/// A new message's job is queued by the message processor itself, and an unacknowledged message is replayed by Kafka,
/// which covers the common failures; this covers the rest, and bounds how long anything can stay pending.
/// </summary>
public sealed class PendingSweeper(
    IStoredMessageStore store,
    IMediaJobPublisher mediaJobs,
    IOutboundJobPublisher outboundJobs,
    ILogger<PendingSweeper> logger,
    IMessageSettledPublisher? settled = null,
    SweepTimings? timings = null)
{
    private readonly SweepTimings _timings = timings ?? new SweepTimings();

    /// <summary>Queues again every file pending too long, and gives up on those pending far too long. Returns how many it acted on.</summary>
    public async Task<int> SweepMediaAsync(DateTime now, CancellationToken cancellationToken)
    {
        IReadOnlyList<StaleMessage> stale = await store.FindStalePendingMediaAsync(now - _timings.MediaStaleAfter, _timings.BatchSize, cancellationToken);
        int acted = 0;
        foreach (StaleMessage message in stale)
        {
            foreach (int index in message.PendingMedia)
            {
                if (now - message.StoredAt > _timings.MediaExpireAfter)
                {
                    IncomingMedia expired = new() { Kind = "unknown", Status = MediaStatus.Failed, FailureReason = MediaFailure.Expired };
                    if (await store.CompleteMediaAsync(message.MessageId, index, expired, cancellationToken))
                    {
                        MessageMetrics.Swept.Add(1, Tag("media"), Action("expired"));
                        logger.LogWarning("sweep.media_expired MessageId={MessageId} MediaIndex={MediaIndex}", message.MessageId, index);
                        await AnnounceAsync(message, SettledKinds.Media, MediaStatus.Failed, cancellationToken);
                    }
                }
                else
                {
                    await mediaJobs.PublishAsync(
                        new MediaJob { MessageId = message.MessageId, Index = index, CorrelationId = "sweep", EnqueuedAt = DateTime.UtcNow }, cancellationToken);
                    MessageMetrics.Swept.Add(1, Tag("media"), Action("requeued"));
                    logger.LogWarning("sweep.media_requeued MessageId={MessageId} MediaIndex={MediaIndex}", message.MessageId, index);
                }

                acted++;
            }
        }

        return acted;
    }

    /// <summary>
    /// Queues again every reply pending too long (or sending since before a worker's lease), and fails those pending far
    /// too long. Returns how many it acted on.
    /// </summary>
    public async Task<int> SweepSendsAsync(DateTime now, CancellationToken cancellationToken)
    {
        IReadOnlyList<StaleMessage> stale = await store.FindStalePendingSendsAsync(
            now - _timings.SendStaleAfter, now - OutboundFileProcessor.Lease, _timings.BatchSize, cancellationToken);
        int acted = 0;
        foreach (StaleMessage reply in stale)
        {
            if (reply.SendStatus == "pending" && now - reply.StoredAt > _timings.SendExpireAfter)
            {
                if (await store.FailPendingSendAsync(reply.MessageId, cancellationToken))
                {
                    MessageMetrics.Swept.Add(1, Tag("send"), Action("expired"));
                    logger.LogWarning("sweep.send_expired MessageId={MessageId}", reply.MessageId);
                    await AnnounceAsync(reply, SettledKinds.Send, "failed", cancellationToken);
                }
            }
            else if (reply.ConnectionId is { } connection)
            {
                // A reply "sending" past the lease is settled unknown by the job, which never sends it again.
                await outboundJobs.PublishAsync(
                    new OutboundJob { MessageId = reply.MessageId, ThreadKey = OutboundJob.KeyFor(connection, reply.Recipient), CorrelationId = "sweep", EnqueuedAt = DateTime.UtcNow },
                    cancellationToken);
                MessageMetrics.Swept.Add(1, Tag("send"), Action("requeued"));
                logger.LogWarning("sweep.send_requeued MessageId={MessageId} Status={Status}", reply.MessageId, reply.SendStatus);
            }

            acted++;
        }

        return acted;
    }

    private async Task AnnounceAsync(StaleMessage message, string kind, string status, CancellationToken cancellationToken)
    {
        if (settled is null || message.OwnerUserId is not { Length: > 0 } owner || message.ConnectionId is not { } connection) return;
        await settled.PublishAsync(
            new MessageSettled { Kind = kind, MessageId = message.MessageId, OwnerUserId = owner, ConnectionId = connection, Customer = message.Recipient, Status = status },
            cancellationToken);
    }

    private static KeyValuePair<string, object?> Tag(string kind) => new("kind", kind);

    private static KeyValuePair<string, object?> Action(string action) => new("action", action);
}
