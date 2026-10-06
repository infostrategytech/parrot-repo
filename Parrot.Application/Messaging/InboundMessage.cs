using Parrot.Domain.Enums;

namespace Parrot.Application.Messaging;

/// <summary>
/// One customer message as the webhook hands it to Kafka: where it came from
/// (<see cref="Source"/>) and the provider's payload untouched
/// (<see cref="Data"/>). The webhook only reads what routing needs (the ids,
/// the two participants, the time); the Message Worker decodes the rest.
///
/// Published to the inbound topic with <see cref="ConversationKey"/> as the
/// Kafka key, so one conversation's messages stay in order.
/// </summary>
public sealed record InboundMessage
{
    /// <summary>Bumped when a field changes meaning, so a worker can tell old messages from new.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>
    /// "{platform}:{provider message id}", e.g. "whatsapp:wamid.HBgN…". Built from
    /// the provider's id rather than generated, so a webhook the provider sends
    /// twice carries the same id and is stored once.
    /// </summary>
    required public string MessageId { get; init; }

    /// <summary>Where the message came from. Written as text on the wire, e.g. "whatsapp".</summary>
    required public MessagingPlatform Source { get; init; }

    /// <summary>The participant who sent it, normalized: digits for a phone number, the id for Facebook.</summary>
    required public string Sender { get; init; }

    /// <summary>The participant it was sent to, normalized the same way.</summary>
    required public string Receiver { get; init; }

    /// <summary>See <see cref="Messaging.ConversationKey"/>. Also the Kafka message key.</summary>
    required public string ConversationKey { get; init; }

    /// <summary>The provider's webhook body exactly as received, as a string. Never logged.</summary>
    required public string Data { get; init; }

    /// <summary>When the provider says it was sent, in UTC. Null when it sent nothing usable.</summary>
    public DateTime? SentAt { get; init; }

    /// <summary>When the webhook received it, in UTC.</summary>
    required public DateTime ReceivedAt { get; init; }

    /// <summary>The W3C trace id of the webhook request, carried to every later step and log line.</summary>
    required public string CorrelationId { get; init; }

    /// <summary>The business account (connection) the webhook was addressed to, when known.</summary>
    public Guid? ConnectionId { get; init; }

    /// <summary>The CRM user who owns that connection, when known.</summary>
    public string? OwnerUserId { get; init; }
}
