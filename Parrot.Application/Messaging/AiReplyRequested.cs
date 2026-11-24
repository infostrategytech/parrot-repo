using System.Text.Json;

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

    /// <summary>
    /// Product photos to send after the text, in order: 1 to 3, each an https link to a catalogue
    /// photo with a one-line caption (possibly empty). Null or empty for a text-only reply.
    /// </summary>
    public IReadOnlyList<AiReplyAttachment>? Attachments { get; init; }

    /// <summary>
    /// The agents the AI service's super agent asked for this reply (at most three): agentId, name, avatar, input,
    /// output and status. Present only when the super agent replied. Display data, read by
    /// <see cref="ReplyContributors"/>: kept as it came, so a list Parrot can't read never stops the reply.
    /// </summary>
    public JsonElement? Contributors { get; init; }
}

/// <summary>A photo for <see cref="AiReplyRequested.Attachments"/>: where it is, and its caption.</summary>
public sealed record AiReplyAttachment(string Url, string? Caption);
