namespace Parrot.Web.Components.Pages.Dashboard.Conversations;

/// <summary>
/// One WhatsApp conversation as the inbox holds it: the fields Parrot's
/// conversation API returns, the paging cursor, and the per-conversation
/// composer state (draft, request id, sending, last send error).
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
}

public class ConvMessage
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = string.Empty;
    public bool IsOutgoing { get; set; }
    public string Status { get; set; } = "received";
    public DateTime ReceivedAt { get; set; }
}
