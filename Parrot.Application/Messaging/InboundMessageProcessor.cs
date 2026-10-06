using System.Diagnostics;
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
/// </summary>
public sealed class InboundMessageProcessor(
    InboundMessageDecoders decoders,
    IStoredMessageStore store,
    IConversationSequence sequence,
    IMessageDocumentMapperFactory mappers,
    IConnectionMessageReceipt receipts,
    IMessagePersistedPublisher publisher,
    ILogger<InboundMessageProcessor> logger)
{
    public async Task ProcessAsync(InboundMessage inbound, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        IncomingMessage incoming = decoders.Decode(inbound);

        // A replay: stored on an earlier attempt that then failed to publish or commit.
        MessageDocument? stored = await store.FindAsync(inbound.MessageId, cancellationToken);
        string outcome = "duplicate";

        if (stored is null)
        {
            MessageDocument document = mappers.ToDocument(incoming);
            document.ConversationKey = inbound.ConversationKey;
            document.Receiver = inbound.Receiver;
            document.Sequence = await sequence.NextAsync(inbound.ConversationKey, cancellationToken);

            stored = await store.InsertOrGetAsync(document, cancellationToken);
            outcome = stored.Sequence == document.Sequence ? "stored" : "duplicate";
        }

        // A message id must not move between businesses; that is never a retry problem.
        if (stored.ConnectionId != inbound.ConnectionId || stored.OwnerUserId != inbound.OwnerUserId)
            throw new UnprocessableMessageException("message_id_owned_by_another_connection");

        if (incoming.ConnectionId.HasValue)
        {
            // Idempotent: a repeat finds the receipt already recorded.
            await receipts.RecordAsync(incoming, cancellationToken);
        }

        // Only now, with the write in place. If this throws, the message comes
        // round again, takes the duplicate path and is announced then.
        await publisher.PublishAsync(new MessagePersisted
        {
            Source = inbound.Source,
            Data = stored.ToStoredMessage(),
            CorrelationId = inbound.CorrelationId,
        }, cancellationToken);

        logger.LogInformation(
            "message.persisted MessageId={MessageId} Source={Source} ConnectionId={ConnectionId} Sequence={Sequence} Outcome={Outcome} DurationMs={DurationMs}",
            inbound.MessageId, inbound.Source, inbound.ConnectionId, stored.Sequence, outcome,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }
}
