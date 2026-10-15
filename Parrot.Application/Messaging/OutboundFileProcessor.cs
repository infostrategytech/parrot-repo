using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Parrot.Application.Conversations;
using Parrot.Domain.Entities.Messages;

namespace Parrot.Application.Messaging;

/// <summary>
/// Handles one <see cref="OutboundJob"/>: claims the stored reply, hands its file to the channel, and records
/// accepted, failed or unknown. The API has already answered the agent, so nothing here holds up a request.
///
/// The one rule is that a reply reaches the channel at most once. It is claimed (pending to sending) before the
/// channel is called, and only the call that wins the claim sends. A job that finds the reply "sending" is the
/// replay of a worker that died mid-send; whether the customer got it is not known, so after a lease it is
/// recorded unknown and never sent again, as a send that times out in the API is. Retrying is allowed only for a
/// refusal that is certain to have sent nothing: the upload of the file to the channel failing.
/// </summary>
public sealed class OutboundFileProcessor(
    IStoredMessageStore store,
    IOutboundFileDispatcher dispatcher,
    ILogger<OutboundFileProcessor> logger,
    IMessageSettledPublisher? settled = null,
    IDeliveryReceipts? receipts = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    Func<double>? jitter = null)
{
    /// <summary>How long one try may take: the upload to the channel, then the send.</summary>
    public static readonly TimeSpan AttemptBudget = TimeSpan.FromSeconds(60);

    /// <summary>The waits before the second and third try of a failed upload.</summary>
    public static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10)];

    /// <summary>How long a reply may stay "sending" before its worker is presumed dead. More than every try's budget put together.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly Func<double> _jitter = jitter ?? Random.Shared.NextDouble;

    public async Task ProcessAsync(OutboundJob job, CancellationToken cancellationToken)
    {
        MessageMetrics.OutboundActive.Add(1);
        try
        {
            await RunAsync(job, cancellationToken);
        }
        finally
        {
            MessageMetrics.OutboundActive.Add(-1);
        }
    }

    private async Task RunAsync(OutboundJob job, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        using IDisposable? scope = logger.BeginScope("MessageId={MessageId} CorrelationId={CorrelationId}", job.MessageId, job.CorrelationId);
        MessageMetrics.OutboundQueueWait.Record(Math.Max(0, (DateTime.UtcNow - job.EnqueuedAt).TotalSeconds));

        MessageDocument reply = await store.FindAsync(job.MessageId, cancellationToken)
            ?? throw new UnprocessableMessageException("outbound_message_not_found");
        if (!reply.Outgoing || reply.Attachments is not { Count: 1 }) throw new UnprocessableMessageException("outbound_not_a_file");

        switch (reply.SendStatus)
        {
            case "pending":
                break;
            case "sending":
                await HandleInterruptedAsync(reply, cancellationToken);
                return;
            default:
                logger.LogInformation("outbound.skipped Status={Status}", reply.SendStatus);
                MessageMetrics.OutboundProcessed.Add(1, Tag("skipped"), Reason("not_pending"));
                return;
        }

        if (!await store.ClaimSendAsync(job.MessageId, DateTime.UtcNow, cancellationToken))
        {
            logger.LogInformation("outbound.skipped Reason=claimed_elsewhere");
            MessageMetrics.OutboundProcessed.Add(1, Tag("skipped"), Reason("claimed_elsewhere"));
            return;
        }

        (string status, string? providerId, string code) = await SendAsync(reply, cancellationToken);

        // Not the worker's stopping token: shutting down must not lose how a send that has happened ended.
        using CancellationTokenSource persist = new(TimeSpan.FromSeconds(10));
        bool applied = await store.CompleteSendAsync(job.MessageId, status, providerId, persist.Token);
        MessageMetrics.Sent.Add(1, new KeyValuePair<string, object?>("status", status), new KeyValuePair<string, object?>("meta_error_code", code));
        MessageMetrics.OutboundProcessed.Add(1, Tag(status), Reason(code));
        MessageMetrics.OutboundDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
        logger.LogInformation("outbound.finished Status={Status} Reason={Reason} Applied={Applied} DurationMs={DurationMs}",
            status, code, applied, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        // The channel can report delivered before the reply had its channel id to be found by.
        if (receipts is not null && providerId is not null && reply.ConnectionId is { } connection)
        {
            try { await receipts.ApplyParkedAsync(connection, providerId, persist.Token); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("outbound.parked_receipts_failed Error={Error}", ex.GetType().Name);
            }
        }

        await AnnounceAsync(reply, status, cancellationToken);
    }

    // Up to three tries, and only for a failed upload: nothing has reached the customer, so another try cannot
    // send twice. Anything the channel did with the message itself is final.
    private async Task<(string Status, string? ProviderId, string Code)> SendAsync(MessageDocument reply, CancellationToken stopping)
    {
        for (int attempt = 0; ; attempt++)
        {
            using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            budget.CancelAfter(AttemptBudget);
            try
            {
                FileDispatchResult result = await dispatcher.DispatchAsync(reply, budget.Token);
                if (result.RejectedCode is null) return ("accepted", result.ProviderId, "none");
                if (!result.IsSafeToRetry || attempt >= RetryDelays.Length) return ("failed", null, result.RejectedCode);

                logger.LogInformation("outbound.retry Attempt={Attempt} Reason={Reason}", attempt + 1, result.RejectedCode);
                await _delay(Backoff.Jitter(RetryDelays[attempt], _jitter()), stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                // Shutting down mid-send: it stays "sending", and the next job for it settles it unknown after the lease.
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
            {
                // The call may have reached the channel: unknown, and never retried.
                logger.LogWarning("outbound.uncertain Error={Error}", ex.GetType().Name);
                return ("unknown", null, "none");
            }
        }
    }

    // Found "sending": the worker that took it did not finish. Inside the lease it may still be working, so the
    // bus tries again later; past it, the send's outcome is unknowable and is recorded as such.
    private async Task HandleInterruptedAsync(MessageDocument reply, CancellationToken cancellationToken)
    {
        if (reply.SendClaimedAt is { } claimed && DateTime.UtcNow - claimed < Lease)
            throw new InvalidOperationException("The reply is being sent by another worker.");

        bool applied = await store.CompleteSendAsync(reply.MessageId, "unknown", null, cancellationToken);
        MessageMetrics.Sent.Add(1, new KeyValuePair<string, object?>("status", "unknown"), new KeyValuePair<string, object?>("meta_error_code", "interrupted"));
        MessageMetrics.OutboundProcessed.Add(1, Tag("unknown"), Reason("interrupted"));
        logger.LogWarning("outbound.interrupted Applied={Applied}; a worker stopped mid-send, so delivery is unknown and it is not sent again", applied);
        if (applied) await AnnounceAsync(reply, "unknown", cancellationToken);
    }

    private async Task AnnounceAsync(MessageDocument reply, string status, CancellationToken cancellationToken)
    {
        if (settled is null || reply.OwnerUserId is not { Length: > 0 } owner || reply.ConnectionId is not { } connection) return;
        await settled.PublishAsync(
            new MessageSettled { Kind = SettledKinds.Send, MessageId = reply.MessageId, OwnerUserId = owner, ConnectionId = connection, Customer = reply.From, Status = status },
            cancellationToken);
    }

    private static KeyValuePair<string, object?> Tag(string outcome) => new("outcome", outcome);

    private static KeyValuePair<string, object?> Reason(string reason) => new("reason", reason);
}
