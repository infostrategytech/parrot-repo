using Parrot.Web.Services;

namespace Parrot.Web.Components.Pages.Dashboard.TeachAi;

/// <summary>
/// Opens the shared business-knowledge conversation (with its voice orb)
/// when an agent needs more grounded information: "Teach more" from an
/// agent. Before the first agent, the business now says what it does in
/// "Tell us about your business" (BusinessIntroState) instead. In real mode
/// the knowledge API does not exist yet, so nothing is held back.
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

    /// <summary>The agent being taught, when opened from one: the AI's orientation is about one agent's job.</summary>
    public string? AgentId { get; private set; }

    public async Task RequireAsync(Func<Task> then, bool setsUpAgent = false, string? agentId = null)
    {
        SetsUpAgent = setsUpAgent;
        AgentId = agentId;
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
    public void TeachMore(string? agentId = null)
    {
        if (!knowledge.Available) return;
        AgentId = agentId;
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
