using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Integrations;
using Parrot.Domain.Entities.Messages;

namespace Parrot.Application.Messaging;

/// <summary>
/// Handles one inbound message: decode, store once with the next sequence,
/// record the connection receipt, then announce it on the persisted topic.
///
/// Safe to run again for a message it has already handled, which is what
/// happens after any failure (the offset isn't committed, so Kafka delivers
/// it again): the stored copy is found and announced again, without taking
/// a new sequence or writing a second document.
///
/// Every line it logs carries the message's id, conversation, connection and
/// the webhook's trace id (scope), so one message can be followed from
/// <c>message.published</c> to <c>message.persisted</c> by any of them:
/// <c>message.consumed</c> (picked up), <c>message.stored</c> (MongoDB
/// acknowledged the write), <c>message.persisted</c> (announced for realtime
/// delivery: the last step) or, on the way, <c>message.unprocessable</c> and
/// <c>message.process_failed</c> naming the stage that failed.
/// </summary>
public sealed class InboundMessageProcessor(
    InboundMessageDecoders decoders,
    IStoredMessageStore store,
    IConversationSequence sequence,
    IMessageDocumentMapperFactory mappers,
    IConnectionMessageReceipt receipts,
    IMessagePersistedPublisher publisher,
    ILogger<InboundMessageProcessor> logger,
    IContactNameLookup? contactNames = null)
{
    public async Task ProcessAsync(InboundMessage inbound, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        // A templated scope, not a dictionary: the console formatter prints a dictionary as its type name.
        using IDisposable? scope = logger.BeginScope(
            "MessageId={MessageId} Source={Source} ConnectionId={ConnectionId} ConversationKey={ConversationKey} CorrelationId={CorrelationId}",
            inbound.MessageId, inbound.Source, inbound.ConnectionId, inbound.ConversationKey, inbound.CorrelationId);

        // ReceivedAt is stamped by the webhook just before it queues the message.
        double queueWaitSeconds = Math.Max(0, (DateTime.UtcNow - inbound.ReceivedAt).TotalSeconds);
        MessageMetrics.QueueWait.Record(queueWaitSeconds, new KeyValuePair<string, object?>("queue", MessageMetrics.InboundQueue));
        logger.LogInformation("message.consumed QueueWaitMs={QueueWaitMs}", (long)(queueWaitSeconds * 1000));

        try
        {
            IncomingMessage incoming = Timed("decode", () => decoders.Decode(inbound));

            long persistStarted = Stopwatch.GetTimestamp();
            (MessageDocument stored, string outcome) = await TimedAsync("persist", () => StoreAsync(inbound, incoming, cancellationToken));

            // The write is acknowledged by MongoDB (or already there from an earlier delivery).
            logger.LogInformation(
                "message.stored Outcome={Outcome} Sequence={Sequence} Type={Type} DurationMs={DurationMs}",
                outcome, stored.Sequence, incoming.Type, (long)Stopwatch.GetElapsedTime(persistStarted).TotalMilliseconds);

            // A message id must not move between businesses; that is never a retry problem.
            if (stored.ConnectionId != inbound.ConnectionId || stored.OwnerUserId != inbound.OwnerUserId)
                throw new UnprocessableMessageException("message_id_owned_by_another_connection");

            if (incoming.ConnectionId.HasValue)
            {
                // Idempotent: a repeat finds the receipt already recorded.
                await TimedAsync("receipt", () => receipts.RecordAsync(incoming, cancellationToken), "receipt_failed");
            }

            // Only now, with the write in place. If this throws, the message comes
            // round again, takes the duplicate path and is announced then.
            MessagePersisted persisted = new MessagePersisted
            {
                Source = inbound.Source,
                Data = stored.ToStoredMessage(),
                CorrelationId = inbound.CorrelationId,
            };
            await TimedAsync("publish", () => publisher.PublishAsync(persisted, cancellationToken), "publish_failed");

            Processed("success", "none");
            logger.LogInformation(
                "message.persisted MessageId={MessageId} Source={Source} ConnectionId={ConnectionId} Sequence={Sequence} Outcome={Outcome} DurationMs={DurationMs}",
                inbound.MessageId, inbound.Source, inbound.ConnectionId, stored.Sequence, outcome,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (UnprocessableMessageException ex)
        {
            // The bus moves it to the dead-letter topic. "unknown_source:sms" -> "unknown_source".
            Processed("dead_lettered", ex.Reason.Split(':')[0]);
            logger.LogWarning("message.unprocessable Reason={Reason}; it goes to the dead-letter topic", ex.Reason);
            throw;
        }
        catch (StageFailedException ex)
        {
            // The bus retries it. The bus logs the stack; this says which message and which stage.
            Processed("failed", ex.Reason);
            logger.LogWarning("message.process_failed Stage={Stage} Reason={Reason} Error={Error}; it is retried",
                ex.Stage, ex.Reason, ex.InnerException!.GetType().Name);
            ExceptionDispatchInfo.Throw(ex.InnerException!);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Processed("failed", "other");
            logger.LogWarning("message.process_failed Stage={Stage} Reason={Reason} Error={Error}; it is retried",
                "process", "other", ex.GetType().Name);
            throw;
        }
    }

    // A duplicate is another delivery of a stored message: Meta sending it again
    // (a new webhook, so a new ReceivedAt). A retry or a rewound batch reads the
    // same Kafka record again, with the ReceivedAt already stored; that message
    // was counted when it was persisted, so it is not counted again. MongoDB keeps
    // milliseconds, hence the tolerance.
    private static void CountDuplicate(MessageDocument stored, InboundMessage inbound)
    {
        if (Math.Abs((stored.ReceivedAt - inbound.ReceivedAt).TotalMilliseconds) >= 1)
        {
            MessageMetrics.Deduplicated.Add(1);
        }
    }

    private static T Timed<T>(string stage, Func<T> action)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            return action();
        }
        finally
        {
            RecordStage(stage, started);
        }
    }

    private static async Task<T> TimedAsync<T>(string stage, Func<Task<T>> action)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            return await action();
        }
        finally
        {
            RecordStage(stage, started);
        }
    }

    // A failure here is retried by the bus; the reason says which step failed.
    private static async Task TimedAsync(string stage, Func<Task> action, string failureReason)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not UnprocessableMessageException)
        {
            throw new StageFailedException(stage, failureReason, ex);
        }
        finally
        {
            RecordStage(stage, started);
        }
    }

    private static void RecordStage(string stage, long started) =>
        MessageMetrics.ProcessingDuration.Record(
            Stopwatch.GetElapsedTime(started).TotalSeconds,
            new KeyValuePair<string, object?>("stage", stage));

    private static void Processed(string status, string reason) =>
        MessageMetrics.Processed.Add(
            1,
            new KeyValuePair<string, object?>("status", status),
            new KeyValuePair<string, object?>("reason", reason));

    // "stored" when this call wrote the document, "duplicate" when it was already there.
    private async Task<(MessageDocument Stored, string Outcome)> StoreAsync(
        InboundMessage inbound, IncomingMessage incoming, CancellationToken cancellationToken)
    {
        try
        {
            // A replay: stored on an earlier attempt that then failed to publish or commit.
            MessageDocument? stored = await store.FindAsync(inbound.MessageId, cancellationToken);
            if (stored is not null)
            {
                CountDuplicate(stored, inbound);
                return (stored, "duplicate");
            }

            // Messenger and Instagram send only the customer's id; their name is asked for once, here,
            // so the inbox can show it. A duplicate above has its name already.
            if (incoming.ContactName is null && contactNames is not null)
            {
                string? name = await TimedAsync("contact_name", () => contactNames.FindAsync(incoming, cancellationToken));
                incoming = incoming with { ContactName = name };
            }

            MessageDocument document = mappers.ToDocument(incoming);
            document.ConversationKey = inbound.ConversationKey;
            document.Receiver = inbound.Receiver;
            document.Sequence = await sequence.NextAsync(inbound.ConversationKey, cancellationToken);

            stored = await store.InsertOrGetAsync(document, cancellationToken);
            if (stored.Sequence != document.Sequence)
            {
                CountDuplicate(stored, inbound);
                return (stored, "duplicate");
            }

            // Only after the insert is acknowledged.
            MessageMetrics.Persisted.Add(1, MessageMetrics.MessageType(incoming.Type));
            return (stored, "stored");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not UnprocessableMessageException)
        {
            string reason = MessageMetrics.Classify(ex);
            MessageMetrics.PersistFailed.Add(1, new KeyValuePair<string, object?>("reason", reason));
            throw new StageFailedException("persist", reason, ex);
        }
    }

    // Carries a failed stage's reason to the one place that counts it; the
    // original exception is rethrown unchanged, so the bus sees what it always did.
    private sealed class StageFailedException(string stage, string reason, Exception inner) : Exception(inner.Message, inner)
    {
        public string Stage { get; } = stage;

        public string Reason { get; } = reason;
    }
}
