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
    public bool Sending { get; set; }
    public string? SendError { get; set; }
    public List<ConvMessage> Messages { get; set; } = new();

    /// <summary>whatsapp, instagram or facebook. Parrot connects WhatsApp only, so that is the default.</summary>
    public string Channel { get; set; } = "whatsapp";
    public int? UnreadCount { get; set; }
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

    public string CustomerInitials => Customer?.Initials ?? Initials.From(CustomerName);
}

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
    public bool KnowledgeUpdated { get; set; }
    public bool Answered { get; set; }
    public string? Answer { get; set; }
}
