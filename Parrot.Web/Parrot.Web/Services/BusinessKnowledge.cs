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
/// What a business says about itself before its first agent (designer and
/// team, 14 October 2026): a summary in its own words, typed or from a voice
/// note, and, if it wants, links (its website, social media) and files (a
/// price list, a menu). The summary is all it needs to save. Links are read
/// by the business-setup enrichment; files by name, frontend only until the
/// API can take uploads (see knowledge-backend-requests).
/// </summary>
public sealed record BusinessIntro(string Summary, IReadOnlyList<string> Links, IReadOnlyList<string> Files);

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

    /// <summary>Saves what the business said about itself; from then on it has a base.</summary>
    Task SaveIntroAsync(BusinessIntro intro);

    /// <summary>
    /// Whether what the business says about itself ("Tell us about your
    /// business", "Add to business knowledge") can be kept. Off, those ways
    /// in are hidden rather than failing.
    /// </summary>
    bool CanSaveIntro => Available;

    /// <summary>
    /// What is already known about the business (its connected channels'
    /// profiles), written as a few lines in its own voice, to start "Tell us
    /// about your business" from; null when nothing is known yet.
    /// </summary>
    Task<string?> StartingPointAsync();
}

/// <summary>Real mode: no knowledge endpoint yet, so agents are not held back.</summary>
public sealed class UnavailableBusinessKnowledge : IBusinessKnowledge
{
    public bool Available => false;
    public Task<bool> HasApprovedAsync() => Task.FromResult(true);
    public Task<IReadOnlyList<KnowledgeCard>> GetDraftAsync() => Task.FromResult<IReadOnlyList<KnowledgeCard>>([]);
    public Task ApproveAsync(IReadOnlyList<KnowledgeCard> cards) => throw new NotSupportedException();
    public Task SaveIntroAsync(BusinessIntro intro) => throw new NotSupportedException();
    public Task<string?> StartingPointAsync() => Task.FromResult<string?>(null);
}
