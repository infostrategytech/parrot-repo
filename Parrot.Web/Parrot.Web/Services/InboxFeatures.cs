namespace Parrot.Web.Services;

/// <summary>
/// The inbox actions in the design that Parrot's API has no endpoint for yet:
/// assignment, taking over from the AI agent, internal notes, AI drafts,
/// saved replies and teaching the AI. Replies and reading still go through
/// <see cref="ConversationClient"/>; after an action the inbox reads the
/// conversation again through that same path.
///
/// When <see cref="Available"/> is false (every real deployment today) the
/// inbox hides these controls rather than showing ones that cannot work.
/// docs: inbox-backend-requests.md lists the endpoints they need.
/// </summary>
public interface IInboxFeatures
{
    bool Available { get; }

    Task AssignToMeAsync(Guid connection, string sender);
    Task UnassignAsync(Guid connection, string sender);
    /// <summary>Take over from the AI agent; null minutes means until the person hands it back.</summary>
    Task TakeOverAsync(Guid connection, string sender, int? minutes);
    Task PauseAiAsync(Guid connection, string sender);
    Task ReturnToAiAsync(Guid connection, string sender);
    /// <summary>Marks the conversation unread again, so it shows as waiting until someone opens it.</summary>
    Task MarkUnreadAsync(Guid connection, string sender);

    Task AddNoteAsync(Guid connection, string sender, string text);
    Task<InboxDraft> DraftReplyAsync(Guid connection, string sender);

    /// <summary>A better answer than the one the AI gave; the customer's copy is not changed.</summary>
    Task CorrectAsync(Guid connection, string sender, string messageId, string correction);
    /// <summary>Answers the AI's question. While the AI controls replies, it replies quoting the original
    /// customer message linked to this question (not the latest inbound message). Otherwise returns the
    /// draft for the human to review/send. The resulting AI message, read through ConversationClient,
    /// must include its real ReplyTo metadata. Conversation history must also return one System event
    /// with the acting person's name and completion time; the question itself is not rendered as a card.</summary>
    Task<string> AnswerAsync(Guid connection, string sender, string messageId, string answer);
    /// <summary>Explicit Done: closes the AI's question without sending a reply or learning an answer.
    /// History returns one System event naming who marked it done and when, not an answered-question card.</summary>
    Task ResolveQuestionAsync(Guid connection, string sender, string messageId);

    Task<IReadOnlyList<InboxSavedReply>> GetSavedRepliesAsync();
    Task<InboxSavedReply> CreateSavedReplyAsync(string title, string body);
    Task<InboxSavedReply> UpdateSavedReplyAsync(string id, string title, string body);
    Task DeleteSavedReplyAsync(string id);
}

public sealed record InboxDraft(string Text, string AgentName);

public sealed record InboxSavedReply(string Id, string Title, string Body);

/// <summary>Real mode: none of the actions above exist on the API yet.</summary>
public sealed class UnavailableInboxFeatures : IInboxFeatures
{
    public bool Available => false;

    private static Task Fail() => throw new NotSupportedException("This inbox action is not available yet.");

    public Task AssignToMeAsync(Guid connection, string sender) => Fail();
    public Task UnassignAsync(Guid connection, string sender) => Fail();
    public Task TakeOverAsync(Guid connection, string sender, int? minutes) => Fail();
    public Task PauseAiAsync(Guid connection, string sender) => Fail();
    public Task ReturnToAiAsync(Guid connection, string sender) => Fail();
    public Task MarkUnreadAsync(Guid connection, string sender) => Fail();
    public Task AddNoteAsync(Guid connection, string sender, string text) => Fail();
    public Task<InboxDraft> DraftReplyAsync(Guid connection, string sender) => throw new NotSupportedException();
    public Task CorrectAsync(Guid connection, string sender, string messageId, string correction) => Fail();
    public Task<string> AnswerAsync(Guid connection, string sender, string messageId, string answer) => throw new NotSupportedException();
    public Task ResolveQuestionAsync(Guid connection, string sender, string messageId) => throw new NotSupportedException();
    public Task<IReadOnlyList<InboxSavedReply>> GetSavedRepliesAsync() => Task.FromResult<IReadOnlyList<InboxSavedReply>>([]);
    public Task<InboxSavedReply> CreateSavedReplyAsync(string title, string body) => throw new NotSupportedException();
    public Task<InboxSavedReply> UpdateSavedReplyAsync(string id, string title, string body) => throw new NotSupportedException();
    public Task DeleteSavedReplyAsync(string id) => Fail();
}
