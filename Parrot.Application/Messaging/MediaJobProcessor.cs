using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Parrot.Application.DTOs.Integrations;
using Parrot.Domain.Entities.Messages;

namespace Parrot.Application.Messaging;

/// <summary>
/// Handles one <see cref="MediaJob"/>: reads the message back, copies the file into private storage, and records
/// ready, failed or too large on the attachment. The message is already in the inbox, showing the file as
/// loading, so nothing here holds up a conversation.
///
/// Safe to run twice for one job, which is what a replay does: a file that is no longer pending is left alone,
/// and the copy's key depends only on the message and the file's position.
///
/// A file the platform could not serve because it was busy or slow is tried again a few times, because the
/// platform's own link is looked up afresh each try. A refusal is not: it is recorded at once. A message or file
/// that is missing is dead-lettered, since no retry will bring it back.
/// </summary>
public sealed class MediaJobProcessor(
    IStoredMessageStore store,
    IInboundMediaStore mediaStore,
    ILogger<MediaJobProcessor> logger,
    IMediaSettledPublisher? settled = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    /// <summary>The waits before the second and third try. Short: WhatsApp's links last only minutes.</summary>
    public static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10)];

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public async Task ProcessAsync(MediaJob job, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        using IDisposable? scope = logger.BeginScope(
            "MessageId={MessageId} MediaIndex={MediaIndex} CorrelationId={CorrelationId}", job.MessageId, job.Index, job.CorrelationId);
        MessageMetrics.MediaQueueWait.Record(Math.Max(0, (DateTime.UtcNow - job.EnqueuedAt).TotalSeconds));

        MessageDocument document = await store.FindAsync(job.MessageId, cancellationToken)
            ?? throw new UnprocessableMessageException("media_message_not_found");
        MessageAttachment attachment = document.Attachments is { } list && job.Index >= 0 && job.Index < list.Count
            ? list[job.Index]
            : throw new UnprocessableMessageException("media_attachment_not_found");

        if (MediaStatus.Of(attachment) != MediaStatus.Pending)
        {
            Finished("skipped", "not_pending", started);
            logger.LogInformation("media.skipped Status={Status}", MediaStatus.Of(attachment));
            return;
        }

        IncomingMessage message = new()
        {
            MessageId = document.MessageId, Platform = document.Platform, ConnectionId = document.ConnectionId,
            OwnerUserId = document.OwnerUserId, ExternalAccountId = document.ExternalAccountId, From = document.From,
            Timestamp = document.Timestamp, Type = document.Type, ChannelId = document.ChannelId, ChannelDisplayName = "",
            Media = [.. document.Attachments!.Select(a => ToIncoming(a, a == attachment))],
        };

        IncomingMedia outcome;
        for (int attempt = 0; ; attempt++)
        {
            outcome = await mediaStore.SaveFileAsync(message, job.Index, cancellationToken);
            if (outcome.Status != MediaStatus.Failed || !MediaFailure.IsWorthRetrying(outcome.FailureReason) || attempt >= RetryDelays.Length) break;

            logger.LogInformation("media.retry Attempt={Attempt} Reason={Reason}", attempt + 1, outcome.FailureReason);
            await _delay(RetryDelays[attempt], cancellationToken);
        }

        // A job that raced another to the same file finds it already settled: the first outcome stands.
        bool applied = await store.CompleteMediaAsync(job.MessageId, job.Index, outcome, cancellationToken);
        string status = outcome.Status ?? MediaStatus.Failed;

        // Only the call that settled it tells the inbox; a skipped one found it settled and was announced already.
        if (applied && settled is not null && document.OwnerUserId is { Length: > 0 } owner && document.ConnectionId is { } connection)
        {
            await settled.PublishAsync(
                new MediaSettled
                {
                    MessageId = document.MessageId, OwnerUserId = owner, ConnectionId = connection, Customer = document.From,
                    Status = status, CorrelationId = job.CorrelationId,
                },
                cancellationToken);
        }

        Finished(applied ? status : "skipped", outcome.FailureReason ?? "none", started);
        logger.LogInformation("media.finished Status={Status} Reason={Reason} Applied={Applied} DurationMs={DurationMs}",
            status, outcome.FailureReason ?? "none", applied, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    // Only the file being saved is looked at; the others are kept in position so its index and key stay the same.
    private static IncomingMedia ToIncoming(MessageAttachment a, bool target) => new()
    {
        Kind = a.Kind, ContentType = a.ContentType, FileName = a.FileName, SizeBytes = a.SizeBytes, Caption = a.Caption,
        ProviderMediaId = target ? a.ProviderMediaId : null, SourceUrl = target ? a.SourceUrl : null,
        StorageKey = a.StorageKey, Status = a.Status, FailureReason = a.FailureReason,
    };

    private static void Finished(string outcome, string reason, long started)
    {
        MessageMetrics.MediaProcessed.Add(1,
            new KeyValuePair<string, object?>("outcome", outcome), new KeyValuePair<string, object?>("reason", reason));
        MessageMetrics.MediaDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
    }
}
