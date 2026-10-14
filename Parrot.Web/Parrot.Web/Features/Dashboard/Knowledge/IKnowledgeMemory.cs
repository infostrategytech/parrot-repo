using Parrot.Web.Services;

namespace Parrot.Web.Features.Dashboard.Knowledge;

/// <summary>Where a fact stands: the business said so, or it was found and waits for a yes.</summary>
public enum FactState { Known, ToConfirm }

/// <summary>
/// One thing the AI knows about the business, in a plain sentence ("We
/// deliver across Lagos in 1 to 2 days"), with where it came from.
/// </summary>
/// <param name="Topic">The business knowledge topic it belongs to, as the agents and "Tell us about your business" know them.</param>
/// <param name="Source">Where it was found ("your Instagram bio"); null when the business told us.</param>
/// <param name="Label">
/// The heading it is listed under, taken from what it says ("Delivery",
/// "Product care", "Accounts"), so the list fits any business rather than a
/// fixed set of shelves (research, 12 October 2026: Claude's memory topics,
/// Intercom's topics, Ada). The owner can move it under another.
/// </param>
public sealed record KnowledgeFact(string Id, KnowledgeTopic Topic, string Text, FactState State, string? Source, DateTimeOffset UpdatedAt, string Label);

/// <summary>A question customers asked that the AI had no answer for, and how often.</summary>
public sealed record KnowledgeQuestion(string Id, string Question, int Asked, DateTimeOffset LastAsked, string Channel);

/// <summary>Two places that say different things about the same fact ("₦2,000" and "₦1,500"): the business picks.</summary>
public sealed record KnowledgeConflict(string Id, KnowledgeTopic Topic, string Question, IReadOnlyList<KnowledgeClaim> Claims);

/// <summary>One side of a conflict: what it says, and where.</summary>
public sealed record KnowledgeClaim(string Text, string Source);

/// <summary>Everything the page shows at once.</summary>
public sealed record KnowledgeSnapshot(
    IReadOnlyList<KnowledgeFact> Facts,
    IReadOnlyList<KnowledgeQuestion> Questions,
    IReadOnlyList<KnowledgeConflict> Conflicts);

/// <summary>
/// The business's knowledge as the Knowledge page manages it, kept as short
/// facts the way ChatGPT keeps memories (designer, 12 October 2026): a list
/// anyone can read, add to, change and remove from. Sorting what the owner
/// writes into topics and short facts is the AI's work. There is no
/// knowledge API yet (a backend request), so real mode uses
/// UnavailableKnowledgeMemory and the screens hide what needs it.
/// </summary>
public interface IKnowledgeMemory
{
    /// <summary>False until the API keeps knowledge; the page shows its placeholder then.</summary>
    bool Available { get; }

    Task<KnowledgeSnapshot> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves what the owner wrote, as one fact or several, each under its
    /// topic (or the topic given). Returns what was saved, so it can be shown
    /// and undone.
    /// </summary>
    Task<IReadOnlyList<KnowledgeFact>> AddAsync(string text, KnowledgeTopic? topic = null, CancellationToken cancellationToken = default);

    Task<KnowledgeFact> UpdateAsync(string id, string text, CancellationToken cancellationToken = default);

    /// <summary>"Yes, that's right": a found fact becomes known.</summary>
    Task ConfirmAsync(string id, CancellationToken cancellationToken = default);

    Task DeleteAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Lists it under another heading (one that exists, or a new one).</summary>
    Task<KnowledgeFact> MoveAsync(string id, string label, CancellationToken cancellationToken = default);

    /// <summary>Puts back what was just removed (Undo).</summary>
    Task RestoreAsync(KnowledgeFact fact, CancellationToken cancellationToken = default);

    /// <summary>Answers a customer's question: the answer is saved as knowledge and the question goes.</summary>
    Task<IReadOnlyList<KnowledgeFact>> AnswerAsync(string questionId, string answer, CancellationToken cancellationToken = default);

    /// <summary>Not something the business wants answered (the agent keeps handing it to the team).</summary>
    Task DismissAsync(string questionId, CancellationToken cancellationToken = default);

    /// <summary>Settles a conflict with the business's own words (one of the claims, or its own).</summary>
    Task<KnowledgeFact> ResolveAsync(string conflictId, string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the business's website for facts its agents should know. What it
    /// finds waits for the owner's yes, in the review, like anything found on
    /// a channel. Returns what was found.
    /// </summary>
    Task<IReadOnlyList<KnowledgeFact>> ReadWebsiteAsync(string url, CancellationToken cancellationToken = default);
}

/// <summary>Real mode: no knowledge API yet.</summary>
public sealed class UnavailableKnowledgeMemory : IKnowledgeMemory
{
    public bool Available => false;
    public Task<KnowledgeSnapshot> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult(new KnowledgeSnapshot([], [], []));
    public Task<IReadOnlyList<KnowledgeFact>> AddAsync(string text, KnowledgeTopic? topic = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<KnowledgeFact> UpdateAsync(string id, string text, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task ConfirmAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<KnowledgeFact> MoveAsync(string id, string label, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task RestoreAsync(KnowledgeFact fact, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<KnowledgeFact>> AnswerAsync(string questionId, string answer, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DismissAsync(string questionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<KnowledgeFact> ResolveAsync(string conflictId, string text, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<KnowledgeFact>> ReadWebsiteAsync(string url, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
