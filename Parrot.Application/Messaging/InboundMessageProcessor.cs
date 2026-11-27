using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Parrot.Application.Agents;
using Parrot.Application.Conversations;
using Parrot.Application.Customers;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Integrations;
using Parrot.Domain.Entities.Messages;
using Parrot.Domain.Enums;

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
    IContactNameLookup? contactNames = null,
    IInboundMediaStore? mediaStore = null,
    IMediaJobPublisher? mediaJobs = null,
    bool deferMedia = false,
    ICustomerRepository? customerRepository = null,
    IAgentChannelLookup? agentChannelLookup = null,
    ITenantIdLookup? tenantIdLookup = null,
    IHumanTakeoverStore? humanTakeover = null,
    IMediaLinkSigner? mediaLinks = null)
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

            // One job per file still pending, with the write in place. A replay finds them still pending and
            // queues them again, which is safe: the media worker skips a file that is already settled.
            if (mediaJobs is not null && stored.Attachments is { Count: > 0 } files)
            {
                await TimedAsync("media_enqueue", () => EnqueueMediaAsync(inbound, stored, files, cancellationToken), "media_enqueue_failed");
            }

            // Only now, with the write in place. If this throws, the message comes
            // round again, takes the duplicate path and is announced then.
            CommsType commsType = await ResolveCommsTypeAsync(stored, cancellationToken);
            string? tenantId = await ResolveTenantIdAsync(stored, cancellationToken);
            MessagePersisted persisted = new MessagePersisted
            {
                Source = inbound.Source,
                Data = stored.ToStoredMessage(mediaLinks) with { CommsType = commsType, TenantId = tenantId },
                CorrelationId = inbound.CorrelationId,
            };
            await TimedAsync("publish", () => publisher.PublishAsync(persisted, cancellationToken), "publish_failed");

            // Best-effort: a failure here never retries the message or blocks the consumer.
            if (customerRepository is not null && stored.OwnerUserId is not null && stored.ConnectionId.HasValue && !stored.Outgoing)
            {
                try
                {
                    await customerRepository.UpsertAsync(
                        stored.OwnerUserId,
                        stored.ConnectionId.Value,
                        stored.Platform.ToString().ToLowerInvariant(),
                        stored.From,
                        stored.ContactName is { Length: > 0 } ? stored.ContactName : stored.From,
                        stored.ContactUsername,
                        stored.ReceivedAt,
                        cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "customer_upsert_failed Sender={Sender} ConnectionId={ConnectionId}; continuing",
                        stored.From, stored.ConnectionId);
                }
            }

            Processed("success", "none");
            logger.LogInformation(
                "message.persisted MessageId={MessageId} Source={Source} ConnectionId={ConnectionId} Sequence={Sequence} Outcome={Outcome} CommsType={CommsType} DurationMs={DurationMs}",
                inbound.MessageId, inbound.Source, inbound.ConnectionId, stored.Sequence, outcome, commsType,
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
                ContactProfile? profile = await TimedAsync("contact_name", () => contactNames.FindAsync(incoming, cancellationToken));
                incoming = incoming with { ContactName = profile?.Name, ContactUsername = profile?.Username };
            }

            // Copied before the sequence is taken, and only for a message not stored yet, so a duplicate
            // never downloads twice. The platform's links expire, which is why it happens here.
            if (incoming.Media is { Count: > 0 } && deferMedia && mediaJobs is not null)
            {
                // Stored at once, the files loading: a job per file copies them after, so a slow platform never
                // holds the message back. The link stays on the file for the job to use, and is dropped when it is done.
                incoming = incoming with
                {
                    Media = [.. incoming.Media.Select(m => m.Kind == MediaKinds.Link ? m : m with { Status = MediaStatus.Pending })],
                };
            }
            else if (incoming.Media is { Count: > 0 } && mediaStore is not null)
            {
                IReadOnlyList<IncomingMedia> saved = await TimedAsync("media", () => mediaStore.SaveAsync(incoming, cancellationToken));
                incoming = incoming with { Media = saved };
            }

            MessageDocument document = mappers.ToDocument(incoming);
            document.ConversationKey = inbound.ConversationKey;
            document.Receiver = inbound.Receiver;
            if (document.ReplyTo is { } reply) document.ReplyTo = await QuotedAsync(document, reply, cancellationToken);
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

    private async Task EnqueueMediaAsync(
        InboundMessage inbound, MessageDocument stored, List<MessageAttachment> files, CancellationToken cancellationToken)
    {
        for (int index = 0; index < files.Count; index++)
        {
            if (MediaStatus.Of(files[index]) != MediaStatus.Pending) continue;
            await mediaJobs!.PublishAsync(
                new MediaJob { MessageId = stored.MessageId, Index = index, CorrelationId = inbound.CorrelationId, EnqueuedAt = DateTime.UtcNow },
                cancellationToken);
        }
    }

    // The customer quoted one of the conversation's messages: theirs (stored under "{platform}:{id}") or one of
    // our replies (found by the id the channel gave it). Kept as a copy, so the quote shows with the message.
    // Not found (older than the inbox, or another conversation's), the reply keeps only the channel's id. A
    // failed lookup never fails the message: it is stored without the copy.
    private async Task<MessageReplyTo> QuotedAsync(MessageDocument document, MessageReplyTo reply, CancellationToken cancellationToken)
    {
        try
        {
            string id = reply.ProviderMessageId;
            MessageDocument? quoted = await store.FindAsync($"{MessageReplies.Prefix(document.Platform)}:{id}", cancellationToken);
            if (quoted is null && document.ConnectionId is { } connection)
                quoted = await store.FindByProviderIdAsync(connection, id, cancellationToken);
            return quoted is not null && quoted.ConnectionId == document.ConnectionId && quoted.From == document.From
                ? MessageReplies.Quote(quoted, id)
                : reply;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return reply;
        }
    }

    private async Task<string?> ResolveTenantIdAsync(MessageDocument stored, CancellationToken ct)
    {
        if (tenantIdLookup is null || stored.OwnerUserId is null) return stored.OwnerUserId;
        return await tenantIdLookup.FindOwnerIdAsync(stored.OwnerUserId, ct) ?? stored.OwnerUserId;
    }

    private async Task<CommsType> ResolveCommsTypeAsync(MessageDocument stored, CancellationToken ct)
    {
        if (stored.Outgoing) return CommsType.TenantToUser;
        if (agentChannelLookup is null || stored.OwnerUserId is null) return CommsType.UserToTenant;
        string channel = stored.Platform.ToString().ToLowerInvariant();
        bool isLive = await agentChannelLookup.IsAgentLiveAsync(stored.OwnerUserId, channel, stored.ConnectionId, ct);
        if (!isLive) return CommsType.UserToTenant;
        // Agent is live: if a human has taken over this conversation, the message still goes to a human.
        if (humanTakeover is not null && stored.ConnectionId.HasValue && stored.From is not null)
        {
            bool taken = await humanTakeover.IsActiveAsync(stored.OwnerUserId, stored.ConnectionId.Value, stored.From, ct);
            if (taken) return CommsType.UserToTenant;
        }
        return CommsType.UserToAgent;
    }

    // Carries a failed stage's reason to the one place that counts it; the
    // original exception is rethrown unchanged, so the bus sees what it always did.
    private sealed class StageFailedException(string stage, string reason, Exception inner) : Exception(inner.Message, inner)
    {
        public string Stage { get; } = stage;

        public string Reason { get; } = reason;
    }
}
