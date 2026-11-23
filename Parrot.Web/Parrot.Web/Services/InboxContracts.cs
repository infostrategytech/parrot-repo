namespace Parrot.Web.Services;

// What the inbox reads from api/conversations. The positional fields are the
// ones Parrot's API returns today (Parrot.Application.Conversations). The
// init-only fields are the rest of the inbox design, which the API does not
// send yet: they arrive as null and the inbox hides what they drive. They live
// here, in the web app, so the
// API's own contracts stay untouched. docs: inbox-backend-requests.md lists
// each one for the backend.

/// <summary>One row of the conversation list.</summary>
public sealed record InboxConversationDto(Guid ConnectionId, string Sender, string Name, string Preview, DateTime UpdatedAt)
{
    /// <summary>whatsapp, facebook or tiktok. Null reads as WhatsApp.</summary>
    public string? Channel { get; init; }
    /// <summary>Customer messages the signed-in user has not read.</summary>
    public int? UnreadCount { get; init; }
    /// <summary>When the customer last wrote. Starts the reply window.</summary>
    public DateTime? LastCustomerMessageAt { get; init; }
    /// <summary>How long after that the business may reply: 24 hours, 48 on TikTok. Null when the API does not say.</summary>
    public int? ReplyWindowHours { get; init; }
    /// <summary>The business's account on that channel; the Facebook Page id is what its inbox link needs.</summary>
    public string? AccountId { get; init; }
    /// <summary>The customer's handle on the channel without the @ (Instagram); their profile link is built from it.</summary>
    public string? Username { get; init; }
    /// <summary>The signed-in user pinned this conversation: it is listed above the others.</summary>
    public bool Pinned { get; init; }
    /// <summary>The user marked it unread to return to it: the row shows "!" while no message is unread.</summary>
    public bool MarkedUnread { get; init; }
    /// <summary>open, pending or resolved.</summary>
    public string? Status { get; init; }
    /// <summary>normal, high or urgent.</summary>
    public string? Priority { get; init; }
    /// <summary>active (the AI agent replies), handoff (a person has taken over) or paused.</summary>
    public string? AiState { get; init; }
    public InboxPersonDto? AiAgent { get; init; }
    public InboxPersonDto? Assignee { get; init; }
    /// <summary>When a timed take-over ends and the AI agent resumes.</summary>
    public DateTime? ControlUntil { get; init; }
    /// <summary>The AI agent has asked the team a question it could not answer.</summary>
    public bool? NeedsAnswer { get; init; }
    /// <summary>Canonical conversation input/recipient state, not a projection of open or urgent tasks.
    /// Null on older APIs: do not guess a reply obligation from unread counts or partial history.</summary>
    public InboxAttentionDto? Attention { get; init; }
    public InboxCustomerDto? Customer { get; init; }
    /// <summary>The live agent covering this conversation's channel. Null when no agent is connected to it.</summary>
    public InboxPersonDto? ChannelAgent { get; init; }
    /// <summary>Agents eligible to reply on this channel. Null on older providers; keep the single-agent display.</summary>
    public IReadOnlyList<InboxPersonDto>? ChannelAgents { get; init; }
}

/// <summary>Unread customer messages across every conversation and channel.</summary>
public sealed record InboxUnreadDto(int Total);

/// <summary>The responsible human, resolved server-side (including owner fallback). ReplyNeeded clears
/// after a confirmed customer-facing reply or return to AI, independently of the task's lifecycle.
/// HandoverTaskId links the actual handoff work; an ordinary linked order is not a handoff.</summary>
public sealed record InboxAttentionDto(string ResponsiblePersonId, bool ReplyNeeded = false, string? HandoverTaskId = null);

public sealed record InboxPinDto(bool Pinned);

/// <summary>A private note on a conversation. The customer never receives it.</summary>
public sealed record InboxNoteDto(string Id, string Text, string AuthorId, string AuthorName, DateTime CreatedAt);

public sealed record InboxAddNoteDto(string Text);

public sealed record InboxMarkedUnreadDto(bool MarkedUnread);

/// <summary>UpToMessageId is the id of the newest message the agent has seen in the thread.</summary>
public sealed record InboxMarkReadDto(string UpToMessageId);

/// <summary>What is still unread after a mark: in that conversation, and in total.</summary>
public sealed record InboxMarkReadResultDto(int ConversationUnread, int Total);

/// <summary>A teammate or an AI agent. IsMe marks the signed-in person, for the Mine tab.</summary>
public sealed record InboxPersonDto(string Id, string Name)
{
    public bool IsMe { get; init; }
}

public sealed record InboxMessagePageDto(IReadOnlyList<InboxMessageDto> Messages, string? NextBefore);

public sealed record InboxMessageDto(string Id, string Text, bool Outgoing, string Status, DateTime ReceivedAt)
{
    /// <summary>customer, ai, human, note, system or ai_question. Null is read from Outgoing.</summary>
    public string? Kind { get; init; }
    public InboxPersonDto? Author { get; init; }
    /// <summary>Agents actually called for this AI reply, not the channel roster or the service that sent it.</summary>
    public IReadOnlyList<InboxPersonDto>? AgentsInvolved { get; init; }
    public InboxAttachmentDto? Attachment { get; init; }
    /// <summary>The files the customer sent with this message: pictures, stickers, GIFs, video, audio and documents.</summary>
    public IReadOnlyList<InboxMediaDto>? Attachments { get; init; }
    /// <summary>knowledge_updated once a correction or answer has been saved as knowledge.</summary>
    public string? LearningStatus { get; init; }
    /// <summary>open or answered, for an AI question.</summary>
    public string? RequestStatus { get; init; }
    public string? Answer { get; init; }
    /// <summary>The message this one replies to (quotes), or null when it quotes nothing.</summary>
    public InboxQuoteDto? ReplyTo { get; init; }
}

/// <summary>
/// A quoted message, shown above a reply. Id is the quoted message's InboxMessageDto.Id, null when it is not
/// in the inbox. Type says what it was when it has no text ("image", "document", …).
/// </summary>
public sealed record InboxQuoteDto(string? Id, bool Outgoing, string? Text, string? Type);

/// <summary>
/// One file a customer sent. Kind is image, gif, sticker, video, audio, voice, document or link. Url is a
/// short-lived address to load it from (the page to open, for a link); null when the file could not be saved.
/// </summary>
public sealed record InboxMediaDto(string Kind, string? ContentType, string? FileName, long? SizeBytes, string? Url)
{
    /// <summary>pending, ready, failed or too_large. A file with no status is one the API sent before the field existed.</summary>
    public string Status { get; init; } = "ready";
    public bool Available => Url is not null;
    public bool IsPending => Status == "pending";
    public bool IsVisual => Kind is "image" or "gif" or "sticker" or "video";
    public bool IsLink => Kind == "link";
}

/// <summary>A file in the customer's media list, with the message that carried it.</summary>
public sealed record InboxMediaItemDto(string MessageId, DateTime ReceivedAt, InboxMediaDto Media);

public sealed record InboxMediaPageDto(IReadOnlyList<InboxMediaItemDto> Items, string? NextBefore);

/// <summary>Type is image or file; Url is null when there is nothing to preview.</summary>
public sealed record InboxAttachmentDto(string Name, string? Type, string? Url);

/// <summary>The body of a reply. AttachmentName is sent only when the inbox has one; today's API ignores it.</summary>
public sealed record InboxReplyDto(Guid RequestId, string Text)
{
    public string? AttachmentName { get; init; }
    /// <summary>The id of the message this replies to, quoting it; null for a plain reply.</summary>
    public string? ReplyToMessageId { get; init; }
}

/// <summary>A picture or file chosen to send, read into memory so a retry sends the same bytes.</summary>
public sealed record InboxPendingFile(string Name, string ContentType, byte[] Bytes)
{
    /// <summary>The server's limits: 5 MB for a picture, 16 MB for any other file.</summary>
    public const long MaxBytes = 16 * 1024 * 1024;
}

/// <summary>Customer 360: who the customer is, beyond the conversation.</summary>
public sealed record InboxCustomerDto
{
    /// <summary>person or business.</summary>
    public string? Kind { get; init; }
    public string? Initials { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }
    public string? Location { get; init; }
    /// <summary>An IANA zone such as Africa/Lagos, for the customer's local time.</summary>
    public string? TimeZone { get; init; }
    public DateTime? JoinedAt { get; init; }
    public IReadOnlyList<InboxIdentityDto> Identities { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
    public decimal? LifetimeValue { get; init; }
    public int? RequestCount { get; init; }
    public int? ConversationCount { get; init; }
    public IReadOnlyList<InboxCustomerRequestDto> RecentRequests { get; init; } = [];
    public IReadOnlyList<InboxCustomerActivityDto> Activity { get; init; } = [];
    public InboxConsentDto? Consent { get; init; }
}

public sealed record InboxIdentityDto(string Channel, string Handle, bool Verified);

/// <summary>Status is processing, shipped, delivered or refunded.</summary>
public sealed record InboxCustomerRequestDto(string Id, DateTime PlacedAt, decimal Amount, int ItemCount, string Status);

/// <summary>Type is conversation, order or profile.</summary>
public sealed record InboxCustomerActivityDto(string Type, string Title, string Detail, DateTime OccurredAt);

public sealed record InboxConsentDto(bool TransactionalMessaging, bool MarketingEmail, bool MarketingMessaging, DateTime UpdatedAt);
