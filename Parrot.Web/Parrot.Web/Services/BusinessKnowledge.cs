namespace Parrot.Web.Services;

/// <summary>
/// The things every business's AI needs to know before it can answer
/// customers; Handover: who the AI passes a customer to when it cannot help;
/// and Notes: anything else the business adds (what customers often ask, what
/// the AI should never say). Handover and Notes are frontend-only for now;
/// see knowledge-backend-requests.
/// </summary>
public enum KnowledgeTopic { Products, Prices, Delivery, Payment, Hours, Returns, Handover, Notes }

/// <summary>Where a topic's draft stands while the business checks it.</summary>
public enum KnowledgeStatus
{
    /// <summary>Found in a connected channel or the workspace; not yet checked.</summary>
    Found,
    /// <summary>Nothing known yet; the AI asks.</summary>
    Missing,
    /// <summary>The business said it is right, or told us what it should say.</summary>
    Confirmed,
    /// <summary>Left for later.</summary>
    Skipped
}

/// <summary>
/// One topic of the business's base knowledge. Files are what the business
/// attached for it (a price list, a returns policy), by name: frontend only
/// until the API can take uploads; see knowledge-backend-requests.
/// </summary>
public sealed record KnowledgeCard(KnowledgeTopic Topic, string Text, KnowledgeStatus Status, string? Source, IReadOnlyList<string>? Files = null)
{
    /// <summary>Something to go on: words, files, or both.</summary>
    public bool HasContent => Text.Length > 0 || Files is { Count: > 0 };

    public string Title => Topic switch
    {
        KnowledgeTopic.Products => "What you offer",
        KnowledgeTopic.Prices => "Pricing",
        KnowledgeTopic.Delivery => "How customers receive it",
        KnowledgeTopic.Payment => "Payment",
        KnowledgeTopic.Hours => "Availability and location",
        KnowledgeTopic.Handover => "Who takes over",
        KnowledgeTopic.Notes => "Good to know",
        _ => "Changes and refunds",
    };
}

/// <summary>
/// The business's base knowledge: a draft built from what Parrot already knows
/// (the workspace, connected channels' profiles), which the business checks
/// and approves before its first AI agent. Agents are grounded in what is
/// approved. Frontend-only until the API has it; see knowledge-backend-requests.
/// </summary>
public interface IBusinessKnowledge
{
    /// <summary>False until the API supports it; the first-agent check is skipped then.</summary>
    bool Available { get; }

    Task<bool> HasApprovedAsync();

    /// <summary>A draft for every topic: what was found, and where, or Missing.</summary>
    Task<IReadOnlyList<KnowledgeCard>> GetDraftAsync();

    Task ApproveAsync(IReadOnlyList<KnowledgeCard> cards);
}

/// <summary>Real mode: no knowledge endpoint yet, so agents are not held back.</summary>
public sealed class UnavailableBusinessKnowledge : IBusinessKnowledge
{
    public bool Available => false;
    public Task<bool> HasApprovedAsync() => Task.FromResult(true);
    public Task<IReadOnlyList<KnowledgeCard>> GetDraftAsync() => Task.FromResult<IReadOnlyList<KnowledgeCard>>([]);
    public Task ApproveAsync(IReadOnlyList<KnowledgeCard> cards) => throw new NotSupportedException();
}
