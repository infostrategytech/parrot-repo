using Parrot.Application.Conversations;

namespace Parrot.Web.Components.Pages.Dashboard.Conversations;

public class ConversationsState
{
    public List<ConversationItem> Conversations { get; } = new();
    public static readonly List<ConvSearchResult> SearchResults = new();
    public ConversationItem? GetConversation(string id) => Conversations.FirstOrDefault(x => x.Id == id);
    public void Clear() => Conversations.Clear();

    public void Apply(IEnumerable<ConversationSummary> summaries)
    {
        foreach (var summary in summaries)
        {
            string id = $"{summary.ConnectionId}:{summary.Sender}";
            var item = GetConversation(id);
            if (item is null)
            {
                item = new ConversationItem
                {
                    Id = id, ConnectionId = summary.ConnectionId, Sender = summary.Sender,
                    CustomerAvatar = "assets/images/avatars/avatar-1.png", AgentName = "Manual replies", AiHandling = "Manual replies",
                    Channels = new() { new ConvChannel { Key = "whatsapp", Name = "WhatsApp", Handle = summary.Sender } },
                };
                Conversations.Add(item);
            }
            item.CustomerName = summary.Name;
            item.LastMessage = summary.Preview;
            item.UpdatedAt = summary.UpdatedAt;
            item.TimeAgo = summary.UpdatedAt.ToLocalTime().ToString("g");
        }
        Conversations.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
    }

    public static void Merge(ConversationItem item, IEnumerable<ConversationMessage> messages)
    {
        var unique = item.Messages.ToDictionary(m => m.Id);
        foreach (var message in messages)
            unique[message.Id] = new ConvMessage
            {
                Id = message.Id, Text = message.Text, IsOutgoing = message.Outgoing,
                Status = message.Status, ReceivedAt = message.ReceivedAt,
            };
        item.Messages = unique.Values.OrderBy(m => m.ReceivedAt).ThenBy(m => m.Id).ToList();
    }
}
