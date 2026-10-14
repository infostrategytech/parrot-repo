using System.Text.RegularExpressions;

namespace Parrot.Web.Features.Dashboard.AiAgents;

/// <summary>
/// An agent's job description, the way you would brief a new assistant
/// (designer, 13 October 2026): one short summary in the
/// owner's words of what it does, what it never does, and when it calls them.
/// The owner reads and edits it as text; the agent follows it sentence by
/// sentence, and a test reply can say which sentence it followed.
/// </summary>
public sealed partial class AgentBrief
{
    public string Summary { get; set; } = string.Empty;

    public bool IsEmpty => Summary.Trim().Length == 0;

    /// <summary>Its sentences, in order.</summary>
    public IReadOnlyList<string> Sentences() => SentencesOf(Summary);

    /// <summary>Its first sentence: what it's there to do (the agent's description).</summary>
    public string Job => Sentences().FirstOrDefault() ?? string.Empty;

    public AgentBrief Copy() => new() { Summary = Summary };

    /// <summary>The job description as it is kept with the agent and given to it.</summary>
    public string ToInstructions() => Summary.Trim();

    public static IReadOnlyList<string> SentencesOf(string text) =>
        SentenceBreak().Split(text.Trim()).Select(sentence => sentence.Trim()).Where(sentence => sentence.Length > 1).ToList();

    [GeneratedRegex(@"(?<=[.!?])\s+|\n+")]
    private static partial Regex SentenceBreak();
}

/// <summary>The job description after the owner said more, and the sentences added to it.</summary>
public sealed record AgentBriefUpdate(AgentBrief Brief, IReadOnlyList<string> Added);

/// <summary>
/// What the job summary is written from: what the owner said (empty for a
/// suggested summary), the job, the kind of business, and the business's name.
/// </summary>
public sealed record AgentDraftRequest(string UseCase, string? CustomJob, string? Industry, string Business, string? Said = null);

/// <summary>One message in a test chat.</summary>
public sealed record AgentTryTurn(bool FromCustomer, string Text);

/// <summary>A test message, sent as a customer, to an agent with this job description.</summary>
public sealed record AgentTryRequest(AgentBrief Brief, string AgentName, string? UseCase, string? Industry, string Business, IReadOnlyList<AgentTryTurn> History, string Message);

/// <summary>What a reply came from: a sentence of its job summary, the business's knowledge, its products, orders or calendar, or nothing it knows.</summary>
public enum AgentTryBasis { Brief, Knowledge, Products, Orders, Calendar, Unknown }

/// <summary>
/// A test reply: what the agent would say, why, and for a sentence of its
/// job summary, that sentence (so the owner can change it).
/// </summary>
public sealed record AgentTryReply(string Reply, string Why, AgentTryBasis Basis, string? Sentence = null);

/// <summary>
/// Writing and understanding an agent's job description, and answering a test
/// message the way the agent would: an AI's work. Until the agents API does
/// this work (a backend request), BasicAgentBriefing
/// writes it from the owner's words, and a test can't be answered (CanTry).
/// </summary>
public interface IAgentBriefing
{
    bool Available { get; }

    /// <summary>Whether it can answer a test message (Test as a customer shows only then).</summary>
    bool CanTry { get; }

    /// <summary>The kind of business, guessed from what it has taught its AI (an AgentIndustry id), or null.</summary>
    Task<string?> SuggestIndustryAsync(CancellationToken cancellationToken = default);

    /// <summary>The job summary, written from what the owner said (or, with nothing said, a suggested one), the job, the kind of business and what the business has taught.</summary>
    Task<AgentBrief> DraftAsync(AgentDraftRequest request, CancellationToken cancellationToken = default);

    /// <summary>What the owner just said, worked into the job summary: only what is new is added.</summary>
    Task<AgentBriefUpdate> UnderstandAsync(string said, AgentBrief sofar, CancellationToken cancellationToken = default);

    /// <summary>What the agent would reply to a customer's message, following its job summary and the business's knowledge.</summary>
    Task<AgentTryReply> TryAsync(AgentTryRequest request, CancellationToken cancellationToken = default);
}
