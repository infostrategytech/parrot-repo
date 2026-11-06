using Parrot.Application.Agents;
using Parrot.Application.Messaging;
using Parrot.Web.Services;

namespace Parrot.Web.Components.Pages.Dashboard.Conversations;

public class ConversationsState
{
    public List<ConversationItem> Conversations { get; } = new();
    public ConversationItem? GetConversation(string id) => Conversations.FirstOrDefault(x => x.Id == id);
    public void Clear() => Conversations.Clear();

    public void Apply(IEnumerable<InboxConversationDto> summaries)
    {
        foreach (var summary in summaries)
        {
            string id = $"{summary.ConnectionId}:{summary.Sender}";
            var item = GetConversation(id);
            if (item is null)
            {
                item = new ConversationItem { Id = id, ConnectionId = summary.ConnectionId, Sender = summary.Sender };
                Conversations.Add(item);
            }
            item.CustomerName = summary.Name;
            item.LastMessage = summary.Preview;
            item.UpdatedAt = summary.UpdatedAt;
            item.Channel = summary.Channel ?? "whatsapp";
            item.UnreadCount = summary.UnreadCount;
            item.AccountId = summary.AccountId;
            item.Username = summary.Username;
            if (!item.PinSaving) item.Pinned = summary.Pinned;
            item.MarkedUnread = summary.MarkedUnread;
            item.LastCustomerMessageAt = summary.LastCustomerMessageAt;
            item.ReplyWindowHours = summary.ReplyWindowHours;
            item.Status = Parse<ConversationStatus>(summary.Status);
            item.Priority = Parse<ConversationPriority>(summary.Priority);
            item.AiState = Parse<AiState>(summary.AiState);
            // The server now owns AiState: when the AI is back in control, the in-memory escalation is gone too.
            if (item.AiState == AiState.Active) item.ActiveEscalation = null;
            item.AiAgent = summary.AiAgent;
            item.Assignee = summary.Assignee;
            item.ControlUntil = summary.ControlUntil;
            item.NeedsAnswer = summary.NeedsAnswer ?? false;
            item.Customer = summary.Customer;
        }
        Resort();
    }

    /// <summary>The inbox's own order, with the pins above the rest whichever sort is chosen. Newest first is the loaded order.</summary>
    public static IEnumerable<ConversationItem> Order(IEnumerable<ConversationItem> list, ConversationSort sort) => sort switch
    {
        ConversationSort.Oldest => list.OrderByDescending(item => item.Pinned).ThenBy(item => item.UpdatedAt),
        ConversationSort.Priority => list.OrderByDescending(item => item.Pinned)
            .ThenByDescending(item => item.Priority ?? ConversationPriority.Normal).ThenByDescending(item => item.UpdatedAt),
        _ => list,
    };

    /// <summary>Pinned conversations first, then newest first within each group, so new messages land below the pins.</summary>
    public void Resort() => Conversations.Sort((a, b) =>
        a.Pinned != b.Pinned ? (a.Pinned ? -1 : 1) : b.UpdatedAt.CompareTo(a.UpdatedAt));

    /// <summary>Puts the conversation's notes (as the server has them) among its messages, replacing any it held, so a note deleted elsewhere goes too.</summary>
    public static void ApplyNotes(ConversationItem item, IEnumerable<InboxNoteDto> notes)
    {
        var merged = item.Messages.Where(m => m.Kind != MessageKind.Note).ToList();
        merged.AddRange(notes.Select(ToMessage));
        item.Messages = merged.OrderBy(m => m.ReceivedAt).ThenBy(m => m.Id).ToList();
    }

    public static ConvMessage ToMessage(InboxNoteDto note) => new()
    {
        Id = note.Id, Text = note.Text, Kind = MessageKind.Note, IsOutgoing = true, Status = "note",
        ReceivedAt = note.CreatedAt, Author = new InboxPersonDto(note.AuthorId, note.AuthorName),
    };

    public static void Merge(ConversationItem item, IEnumerable<InboxMessageDto> messages)
    {
        var unique = item.Messages.ToDictionary(m => m.Id);
        foreach (var message in messages)
            unique[message.Id] = new ConvMessage
            {
                Id = message.Id, Text = message.Text, IsOutgoing = message.Outgoing,
                Status = message.Status, ReceivedAt = message.ReceivedAt,
                // Today's API sends no kind: its messages are the customer's or
                // a reply from the business's number.
                Kind = message.Kind switch
                {
                    "ai" => MessageKind.Ai,
                    "human" => MessageKind.Human,
                    "note" => MessageKind.Note,
                    "system" => MessageKind.System,
                    "ai_question" => MessageKind.AiQuestion,
                    "customer" => MessageKind.Customer,
                    _ => message.Outgoing ? MessageKind.Human : MessageKind.Customer,
                },
                Author = message.Author,
                Attachment = message.Attachment,
                Attachments = message.Attachments ?? [],
                KnowledgeUpdated = message.LearningStatus == "knowledge_updated",
                Answered = message.RequestStatus == "answered",
                Answer = message.Answer,
                ReplyTo = message.ReplyTo,
            };
        item.Messages = unique.Values.OrderBy(m => m.ReceivedAt).ThenBy(m => m.Id).ToList();
    }

    /// <summary>
    /// The loaded conversation a pushed message belongs to, or null when the
    /// list has not shown it yet. The list groups by the customer, who is the
    /// sender of an incoming message and the receiver of a reply.
    /// </summary>
    public ConversationItem? FindForPush(StoredMessage message)
    {
        string customer = message.Outgoing ? message.Receiver : message.Sender;
        return Conversations.FirstOrDefault(c => c.ConnectionId == message.ConnectionId && c.Sender == customer);
    }

    /// <summary>
    /// Finds the conversation that the orchestrator's conversation key refers to, or null when the
    /// inbox has not loaded it. The key is "{platform}:{min}:{max}" with both participants' normalized ids
    /// sorted ordinally; we match by channel prefix and whether the customer's sender appears as one of them.
    /// </summary>
    public ConversationItem? FindByConversationKey(string? conversationKey)
    {
        if (string.IsNullOrEmpty(conversationKey)) return null;
        string[] parts = conversationKey.Split(':', 3);
        if (parts.Length != 3) return null;
        string platform = parts[0];
        string participantA = parts[1];
        string participantB = parts[2];
        return Conversations.FirstOrDefault(item =>
            item.Channel == platform &&
            (NormalizeSender(item.Sender, platform) == participantA ||
             NormalizeSender(item.Sender, platform) == participantB));
    }

    /// <summary>
    /// Applies an agent activity event to a conversation, updating in-memory approval and escalation
    /// state without a network call. Callers refresh from the server when the AiState badge must change.
    /// </summary>
    public static void ApplyAgentActivity(ConversationItem item, AgentActivityEvent activity)
    {
        switch (activity.Type)
        {
            case AgentActivityTypes.ApprovalRequested:
                if (activity.ApprovalId is not null)
                    item.PendingApproval = new PendingApprovalInfo(activity.ApprovalId, activity.Tool, activity.Reason);
                break;

            case AgentActivityTypes.EscalationOpened:
                if (activity.EscalationId is not null)
                    item.ActiveEscalation = new AgentEscalationInfo(activity.EscalationId, activity.Priority, activity.Problem);
                break;

            case AgentActivityTypes.TurnCompleted:
                item.PendingApproval = null;
                // Carry escalation info from the turn event when escalation.opened hasn't landed yet.
                if (activity.Status is AgentActivityTypes.TurnStatus.Escalated or AgentActivityTypes.TurnStatus.Failed
                    && activity.EscalationId is not null)
                {
                    item.ActiveEscalation ??= new AgentEscalationInfo(activity.EscalationId, activity.Priority, activity.Problem);
                }
                break;
        }
    }

    // WhatsApp phone numbers are stored with their digits; opaque ids for other platforms are kept as-is.
    private static string NormalizeSender(string sender, string platform) =>
        platform == "whatsapp"
            ? new string(sender.Where(char.IsAsciiDigit).ToArray())
            : sender.Trim();

    /// <summary>
    /// Moves a conversation to the top with the pushed message as its preview,
    /// as the next list read would.
    /// </summary>
    public void Touch(ConversationItem item, StoredMessage message)
    {
        // A customer's message opens the reply window again, whatever order pushes arrive in.
        if (!message.Outgoing)
        {
            DateTime wrote = message.SentAt ?? message.PersistedAt;
            if (item.LastCustomerMessageAt is not { } last || wrote > last) item.LastCustomerMessageAt = wrote;
        }
        if (message.PersistedAt < item.UpdatedAt) return;
        item.LastMessage = PushedText(message);
        item.UpdatedAt = message.PersistedAt;
        Resort();
    }

    /// <summary>
    /// Adds a pushed customer message to a loaded thread, without a read.
    /// Returns false when the thread already has it. A repeat must not replace
    /// the copy a read gave, which carries the API's exact time.
    /// </summary>
    public static bool MergePushed(ConversationItem item, StoredMessage message)
    {
        if (message.Outgoing) throw new ArgumentException("A pushed reply carries no send status; read the thread instead.", nameof(message));
        if (item.Messages.Any(m => m.Id == message.MessageId)) return false;

        // As the read API sends an incoming message ("received" is the stored
        // default). PersistedAt is a moment after the webhook's ReceivedAt,
        // which the next read puts in its place.
        Merge(item, [new InboxMessageDto(message.MessageId, message.Text ?? (message.Attachments is { Count: > 0 } ? "" : $"[{message.Type} message]"), false, "received", message.PersistedAt)
        {
            // Described without addresses: the files are readable only through the API, which a read of the
            // thread signs. The thread is read again for a message that carries any.
            Attachments = message.Attachments?.Select(a => new InboxMediaDto(a.Kind, a.ContentType, a.FileName, a.SizeBytes, null) { Status = a.Status }).ToList(),
            ReplyTo = message.ReplyTo is { } quote ? new InboxQuoteDto(quote.Id, quote.Outgoing, quote.Text, quote.Type) : null,
        }]);
        return true;
    }

    /// <summary>What the list row shows for a pushed message: its text, or what kind of file it is.</summary>
    public static string PushedText(StoredMessage message) =>
        message.Text ?? (message.Attachments is { Count: > 0 } files ? MediaKinds.PreviewLabel(files[0].Kind) : $"[{message.Type} message]");

    // Unknown values from a newer API read as "not provided" rather than failing.
    private static T? Parse<T>(string? value) where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: true, out var parsed) ? parsed : null;
}
