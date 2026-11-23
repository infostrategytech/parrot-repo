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
        var listed = summaries.Select(s => $"{s.ConnectionId}:{s.Sender}").ToHashSet();
        // The server lists every conversation with an open question or approval on its first page, so one it no longer
        // lists has none (a chat only an agent's question had brought in, since answered elsewhere).
        foreach (var gone in Conversations.Where(c => !listed.Contains(c.Id)))
        {
            gone.NeedsAnswer = false;
            gone.PendingApproval = null;
        }
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
            // The server owns AiState. Clear derived state that only makes sense while the AI is active.
            if (item.AiState == AiState.Active) item.ActiveEscalation = null;
            if (item.AiState != AiState.Active) item.PendingApproval = null;
            // An approval the server still holds open stays, whatever the AI state; one only pushed keeps the rule above.
            if (summary.PendingApproval is { } approval) item.PendingApproval = new PendingApprovalInfo(approval.ApprovalId, approval.Tool, approval.Reason);
            item.AiAgent = summary.AiAgent ?? summary.ChannelAgent;
            item.Assignee = summary.Assignee;
            item.ControlUntil = summary.ControlUntil;
            item.NeedsAnswer = summary.NeedsAnswer ?? false;
            item.Attention = summary.Attention;
            item.Customer = summary.Customer;
            item.ChannelAgent = summary.ChannelAgent;
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
        // A read that left before a question was closed here must not bring it back.
        foreach (var message in messages.Where(m => m.Kind != "ai_question" || !item.ClosedQuestions.Contains(m.Id)))
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

    /// <summary>The newest page carries every agent question still open here; one it no longer has was closed
    /// elsewhere (done, or answered by a reply), so it leaves the thread.</summary>
    public static void DropClosedQuestions(ConversationItem item, IEnumerable<InboxMessageDto> newestPage)
    {
        HashSet<string> open = newestPage.Where(m => m.Kind == "ai_question").Select(m => m.Id).ToHashSet();
        item.Messages = item.Messages.Where(m => m.Kind != MessageKind.AiQuestion || open.Contains(m.Id)).ToList();
        item.NeedsAnswer = item.Messages.Any(m => m.Kind == MessageKind.AiQuestion && !m.Answered);
    }

    /// <summary>A reply the team sent answers the agent's questions asked before it, as the server counts it:
    /// one the channel took, not one still pending or failed.</summary>
    public static void SettleAgentQuestions(ConversationItem item, InboxMessageDto reply)
    {
        if (!reply.Outgoing || reply.Kind is not (null or "human") || reply.Status is not ("sent" or "accepted" or "delivered" or "read")) return;
        foreach (var question in item.Messages.Where(m => m.Kind == MessageKind.AiQuestion && !m.Answered && m.ReceivedAt < reply.ReceivedAt))
        {
            question.Answered = true;
            item.ClosedQuestions.Add(question.Id);
        }
        item.NeedsAnswer = item.Messages.Any(m => m.Kind == MessageKind.AiQuestion && !m.Answered);
    }

    /// <summary>Only a newly confirmed local send can settle the optimistic reply need. Reading an older
    /// reply from history must never override a newer canonical handoff/attention snapshot.</summary>
    public static void SettleReplyNeed(ConversationItem item, InboxMessageDto reply)
    {
        var latestInbound = item.Messages.Where(m => m.Kind == MessageKind.Customer).Select(m => (DateTime?)m.ReceivedAt)
            .Append(item.LastCustomerMessageAt).Max();
        if (item.Attention is { ReplyNeeded: true } attention && reply.Outgoing
            && (reply.Kind is null or "human") && reply.Status is "sent" or "accepted" or "delivered" or "read"
            && latestInbound is { } last && reply.ReceivedAt >= last)
            item.Attention = attention with { ReplyNeeded = false };
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
                // The AI is waiting for a decision, not generating a reply: approval card takes over from the typing indicator.
                item.AiTurnInProgress = false;
                break;

            case AgentActivityTypes.EscalationOpened:
                if (activity.EscalationId is not null)
                    item.ActiveEscalation = new AgentEscalationInfo(activity.EscalationId, activity.Priority, activity.Problem);
                break;

            case AgentActivityTypes.TurnCompleted:
                item.PendingApproval = null;
                item.AiTurnInProgress = false;
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
