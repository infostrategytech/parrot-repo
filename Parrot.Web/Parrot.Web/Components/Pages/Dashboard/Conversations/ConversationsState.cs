using Parrot.Web.Services;
using Parrot.Application.Messaging;

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
            item.Pinned = summary.Pinned;
            item.MarkedUnread = summary.MarkedUnread;
            item.LastCustomerMessageAt = summary.LastCustomerMessageAt;
            item.ReplyWindowHours = summary.ReplyWindowHours;
            item.Status = Parse<ConversationStatus>(summary.Status);
            item.Priority = Parse<ConversationPriority>(summary.Priority);
            item.AiState = Parse<AiState>(summary.AiState);
            item.AiAgent = summary.AiAgent;
            item.Assignee = summary.Assignee;
            item.ControlUntil = summary.ControlUntil;
            item.NeedsAnswer = summary.NeedsAnswer ?? false;
            item.Customer = summary.Customer;
        }
        Resort();
    }

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
                KnowledgeUpdated = message.LearningStatus == "knowledge_updated",
                Answered = message.RequestStatus == "answered",
                Answer = message.Answer,
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
        item.LastMessage = message.Text ?? $"[{message.Type} message]";
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
        Merge(item, [new InboxMessageDto(message.MessageId, message.Text ?? $"[{message.Type} message]", false, "received", message.PersistedAt)]);
        return true;
    }

    // Unknown values from a newer API read as "not provided" rather than failing.
    private static T? Parse<T>(string? value) where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: true, out var parsed) ? parsed : null;
}
