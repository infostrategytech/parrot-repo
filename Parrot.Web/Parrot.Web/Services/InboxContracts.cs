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
    /// <summary>whatsapp, instagram or facebook. Null reads as WhatsApp, the only channel Parrot connects today.</summary>
    public string? Channel { get; init; }
    public int? UnreadCount { get; init; }
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
    public InboxCustomerDto? Customer { get; init; }
}

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
    public InboxAttachmentDto? Attachment { get; init; }
    /// <summary>knowledge_updated once a correction or answer has been saved as knowledge.</summary>
    public string? LearningStatus { get; init; }
    /// <summary>open or answered, for an AI question.</summary>
    public string? RequestStatus { get; init; }
    public string? Answer { get; init; }
}

/// <summary>Type is image or file; Url is null when there is nothing to preview.</summary>
public sealed record InboxAttachmentDto(string Name, string? Type, string? Url);

/// <summary>The body of a reply. AttachmentName is sent only when the inbox has one; today's API ignores it.</summary>
public sealed record InboxReplyDto(Guid RequestId, string Text)
{
    public string? AttachmentName { get; init; }
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
