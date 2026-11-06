using Parrot.SharedComponents.DesignSystem;
using Parrot.Web.Services;

namespace Parrot.Web.Components.Pages.Dashboard.Conversations;

/// <summary>
/// One conversation as the inbox holds it: the fields Parrot's conversation
/// API returns, the paging cursor, and the per-conversation composer state
/// (draft, request id, sending, last send error).
///
/// The nullable fields below the first group are the rest of the inbox design
/// (see InboxContracts). Parrot's API does not send them yet, so they stay null
/// when the API does not provide them, and the inbox hides what they drive.
/// </summary>
public class ConversationItem
{
    public string Id { get; set; } = "";
    public Guid ConnectionId { get; set; }
    public string Sender { get; set; } = "";
    public string CustomerName { get; set; } = string.Empty;
    public string LastMessage { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
    public string? NextBefore { get; set; }
    public bool HistoryLoaded { get; set; }
    public string Draft { get; set; } = "";
    public Guid? DraftRequestId { get; set; }
    /// <summary>A picture or file chosen to send with the draft. Kept with it, so a retry after switching threads still carries the file.</summary>
    public Parrot.Web.Services.InboxPendingFile? PendingFile { get; set; }
    /// <summary>The message the draft replies to (quotes), as WhatsApp Web keeps it per chat; null for a plain reply.</summary>
    public ConvMessage? ReplyingTo { get; set; }
    public bool Sending { get; set; }
    public string? SendError { get; set; }
    public List<ConvMessage> Messages { get; set; } = new();

    /// <summary>whatsapp, instagram or facebook. Parrot connects WhatsApp only, so that is the default.</summary>
    public string Channel { get; set; } = "whatsapp";
    public int? UnreadCount { get; set; }

    /// <summary>The business's account on the channel, for links into that channel's own inbox.</summary>
    public string? AccountId { get; set; }

    /// <summary>The customer's handle on the channel without the @ (Instagram).</summary>
    public string? Username { get; set; }

    /// <summary>The customer's chat on their own platform, or null when none can be built.</summary>
    public ChannelLink? PlatformLink => ChannelLinks.For(Channel, Sender, AccountId, Username);

    /// <summary>Pinned by the signed-in user: listed above every unpinned conversation.</summary>
    public bool Pinned { get; set; }
    /// <summary>A pin or unpin is being saved. A list read that started before it finished must not undo it.</summary>
    public bool PinSaving { get; set; }

    /// <summary>Marked unread by the user to return to it. Shown as "!" while there are no unread messages.</summary>
    public bool MarkedUnread { get; set; }

    /// <summary>When the customer last wrote (UTC). The reply window runs from here.</summary>
    public DateTime? LastCustomerMessageAt { get; set; }
    public int? ReplyWindowHours { get; set; }

    /// <summary>When a reply stops being allowed, or null when the API sent no window.</summary>
    public DateTime? ReplyWindowEndsAt => LastCustomerMessageAt is { } last && ReplyWindowHours is { } hours ? last.AddHours(hours) : null;

    /// <summary>
    /// The window has ended, so the channel would refuse a reply. False when it is not known: the
    /// server's own check decides then.
    /// </summary>
    public bool ReplyWindowClosed => ReplyWindowEndsAt is { } end && end <= DateTime.UtcNow;
    public ConversationStatus? Status { get; set; }
    public ConversationPriority? Priority { get; set; }
    public AiState? AiState { get; set; }
    public InboxPersonDto? AiAgent { get; set; }
    public InboxPersonDto? Assignee { get; set; }
    public DateTime? ControlUntil { get; set; }
    public bool NeedsAnswer { get; set; }
    public InboxCustomerDto? Customer { get; set; }

    /// <summary>An AI agent owns this conversation, so the AI controls apply.</summary>
    public bool HasAiAgent => AiAgent is not null && AiState is not null;

    /// <summary>Customer replies are held while the AI agent is responding.</summary>
    public bool ReplyLocked => AiState == global::Parrot.Web.Components.Pages.Dashboard.Conversations.AiState.Active;

    /// <summary>An approval the AI agent is waiting on before it can continue. Set from the agentActivity push; cleared when the turn ends.</summary>
    public PendingApprovalInfo? PendingApproval { get; set; }

    /// <summary>An escalation the AI opened for this conversation. Set from the agentActivity push; cleared when the AI becomes active again.</summary>
    public AgentEscalationInfo? ActiveEscalation { get; set; }

    public string CustomerInitials => Customer?.Initials ?? Initials.From(CustomerName);
}

/// <summary>An approval the AI agent asked for before proceeding. Tool and Reason may be absent for older orchestrator versions.</summary>
public sealed record PendingApprovalInfo(string ApprovalId, string? Tool, string? Reason);

/// <summary>An escalation the AI opened: the conversation needs a human. Priority is low / normal / high / urgent.</summary>
public sealed record AgentEscalationInfo(string EscalationId, string? Priority, string? Problem);

public class ConvMessage
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = string.Empty;
    public bool IsOutgoing { get; set; }
    public string Status { get; set; } = "received";
    public DateTime ReceivedAt { get; set; }

    public MessageKind Kind { get; set; } = MessageKind.Customer;
    public InboxPersonDto? Author { get; set; }
    public InboxAttachmentDto? Attachment { get; set; }
    public IReadOnlyList<InboxMediaDto> Attachments { get; set; } = [];
    public bool KnowledgeUpdated { get; set; }
    public bool Answered { get; set; }
    public string? Answer { get; set; }
    /// <summary>The message this one replies to (quotes), or null.</summary>
    public InboxQuoteDto? ReplyTo { get; set; }
}
