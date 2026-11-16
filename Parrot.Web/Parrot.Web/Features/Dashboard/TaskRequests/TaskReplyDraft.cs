namespace Parrot.Web.Features.Dashboard.TaskRequests;

/// <summary>
/// Words to put in a conversation's reply box when it next opens: "Use in reply" on a task's answered question goes to
/// the chat with the answer ready to edit and send. Kept for this browser session only; nothing is sent by itself.
/// </summary>
public sealed class TaskReplyDraft
{
    private readonly Dictionary<string, string> _waiting = [];

    public void Set(string conversationId, string text) => _waiting[conversationId] = text;

    public string? Take(string conversationId) => _waiting.Remove(conversationId, out var text) ? text : null;
}
