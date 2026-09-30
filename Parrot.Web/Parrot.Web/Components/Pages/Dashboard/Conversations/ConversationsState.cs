using Parrot.Web.Services;

namespace Parrot.Web.Components.Pages.Dashboard.Conversations;

public class ConversationsState
{
    public List<ConversationItem> Conversations { get; } = new();
    public ConversationItem? GetConversation(string id) => Conversations.FirstOrDefault(x => x.Id == id);
    public void Clear() => Conversations.Clear();

    /// <summary>Raised after the list is refreshed, so the rail's inbox badge can follow it.</summary>
    public event Action? Changed;

    /// <summary>Conversations with something unread, or null when the API does not say.</summary>
    public int? UnreadConversations => Conversations.Any(c => c.UnreadCount is not null)
        ? Conversations.Count(c => c.UnreadCount > 0)
        : null;

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
            item.Status = Parse<ConversationStatus>(summary.Status);
            item.Priority = Parse<ConversationPriority>(summary.Priority);
            item.AiState = Parse<AiState>(summary.AiState);
            item.AiAgent = summary.AiAgent;
            item.Assignee = summary.Assignee;
            item.ControlUntil = summary.ControlUntil;
            item.NeedsAnswer = summary.NeedsAnswer ?? false;
            item.Customer = summary.Customer;
        }
        Conversations.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
        Changed?.Invoke();
    }

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

    // Unknown values from a newer API read as "not provided" rather than failing.
    private static T? Parse<T>(string? value) where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: true, out var parsed) ? parsed : null;
}
