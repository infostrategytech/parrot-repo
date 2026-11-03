namespace Parrot.Application.Messaging;

/// <summary>
/// Published by Omni-AI on the <c>ai.reply.requested</c> topic when it wants Parrot
/// to send a reply to a customer. Parrot validates the request, routes it to the
/// appropriate channel (WhatsApp, Facebook Messenger, Instagram), and then publishes
/// a <see cref="MessagePersisted"/> event with <c>CommsType = AgentToUser</c>.
/// </summary>
public sealed record AiReplyRequested
{
    /// <summary>The workspace (business) that owns the connection. Maps to the Parrot workspace id.</summary>
    required public string WorkspaceId { get; init; }

    /// <summary>The channel connection to send on (WhatsApp, Facebook Page, Instagram account).</summary>
    required public Guid ConnectionId { get; init; }

    /// <summary>The customer's phone number or platform user id.</summary>
    required public string Recipient { get; init; }

    /// <summary>Idempotency key: Parrot delivers at most once per <c>RequestId</c>.</summary>
    required public Guid RequestId { get; init; }

    /// <summary>The reply text.</summary>
    required public string Text { get; init; }
}
