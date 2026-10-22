namespace Parrot.Web.Features.Dashboard.AiAgents;

/// <summary>
/// The kind of business, kept once for the business (designer, 20 October 2026): chosen in Settings, Workspace,
/// where it starts as the AI's guess from what the business has told it, for the owner to confirm or change.
/// Every agent reads it from here: what it's careful about, the questions to test it with, how its job
/// description is written. Kept for the session until the backend stores it (agents-backend-requests 18).
/// </summary>
public sealed class BusinessKind(IAgentBriefing briefing)
{
    /// <summary>What the owner chose; null until they do.</summary>
    public string? Chosen { get; private set; }

    /// <summary>The AI's guess (an AgentIndustry id); null when it can't tell.</summary>
    public string? Guessed { get; private set; }

    private bool _asked;

    /// <summary>The kind in use: the owner's choice, else the guess.</summary>
    public string? Id => Chosen ?? Guessed;

    public bool IsGuess => Chosen is null && Guessed is not null;

    public event Action? Changed;

    /// <summary>Asks for a guess once, when there is no choice yet.</summary>
    public async Task<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (Chosen is null && !_asked)
        {
            _asked = true;
            try { Guessed = await briefing.SuggestIndustryAsync(cancellationToken); }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or NotSupportedException) { Guessed = null; }
        }
        return Id;
    }

    public void Choose(string? id)
    {
        Chosen = string.IsNullOrWhiteSpace(id) ? null : id;
        Changed?.Invoke();
    }
}
