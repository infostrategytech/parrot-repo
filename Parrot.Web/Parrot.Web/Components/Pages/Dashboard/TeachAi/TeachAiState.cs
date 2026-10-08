using Parrot.Web.Services;

namespace Parrot.Web.Components.Pages.Dashboard.TeachAi;

/// <summary>
/// The first-agent rule: an AI agent needs base knowledge the business has
/// approved. Every way of creating an agent goes through RequireAsync, which
/// either carries straight on or opens "Teach your AI" first and carries on
/// once it is approved. In real mode the knowledge API does not exist yet, so
/// nothing is held back.
/// </summary>
public sealed class TeachAiState(IBusinessKnowledge knowledge)
{
    private Func<Task>? _then;

    public bool IsOpen { get; private set; }

    /// <summary>The conversation so far. Kept when closed, so reopening carries on where it stopped.</summary>
    public TeachAiConversation? Conversation { get; set; }

    /// <summary>The headphones tip shows on the first voice turn only.</summary>
    public bool HeadphonesTipShown { get; set; }

    public event Action? Changed;

    /// <summary>Opened on the way to setting up an agent, so it can say what comes next.</summary>
    public bool SetsUpAgent { get; private set; }

    public async Task RequireAsync(Func<Task> then, bool setsUpAgent = false)
    {
        SetsUpAgent = setsUpAgent;
        if (!knowledge.Available || await knowledge.HasApprovedAsync())
        {
            await then();
            return;
        }
        _then = then;
        IsOpen = true;
        Changed?.Invoke();
    }

    /// <summary>
    /// "Teach more", from an agent: opens Teach your AI on the approved
    /// knowledge to add to it, with nothing waiting on it. Approving again
    /// updates what every agent knows.
    /// </summary>
    public void TeachMore()
    {
        if (!knowledge.Available) return;
        _then = null;
        SetsUpAgent = false;
        Conversation = null;
        IsOpen = true;
        Changed?.Invoke();
    }

    /// <summary>Closed without approving: whatever was asked for does not happen.</summary>
    public void Close()
    {
        IsOpen = false;
        _then = null;
        Changed?.Invoke();
    }

    /// <summary>Approved: close, then carry on with what was asked for.</summary>
    public async Task CompleteAsync()
    {
        var then = _then;
        IsOpen = false;
        _then = null;
        Conversation = null;
        Changed?.Invoke();
        if (then is not null) await then();
    }
}
