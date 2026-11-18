using Parrot.Web.Services;

namespace Parrot.Web.Features.Dashboard.Knowledge;

/// <summary>
/// The business's knowledge as the AI service keeps it (/api/v1/business-knowledge), read as the signed-in
/// person: its published items are what the AI knows, its pending cards are the things to check. Only the
/// Knowledge page uses it, and only to read: changing knowledge waits on the service's rules for who may write.
/// On when the web app's configuration says "OmniAi": { "Knowledge": true } (or OmniAi__Knowledge=true).
/// </summary>
public sealed class OmniKnowledgeMemory(OmniAiClient omni, IConfiguration configuration, ILogger<OmniKnowledgeMemory> logger) : IKnowledgeMemory
{
    /// <summary>The service's answer when business knowledge is switched off on its side.</summary>
    public const string KnowledgeUnavailable = "KNOWLEDGE_UNAVAILABLE";

    private const string Root = "api/v1/business-knowledge";

    // The service's ten sections, named as the page shows them; the topic picks the icon.
    private static readonly Dictionary<string, (string Label, KnowledgeTopic Topic)> Sections = new()
    {
        ["about"] = ("About the business", KnowledgeTopic.Products),
        ["products"] = ("Products", KnowledgeTopic.Products),
        ["prices_offers"] = ("Prices and offers", KnowledgeTopic.Prices),
        ["delivery"] = ("Delivery", KnowledgeTopic.Delivery),
        ["payment"] = ("Payment", KnowledgeTopic.Payment),
        ["hours_contact"] = ("Hours and contact", KnowledgeTopic.Hours),
        ["returns"] = ("Returns", KnowledgeTopic.Returns),
        ["product_care"] = ("Product care", KnowledgeTopic.Notes),
        ["handover"] = ("Handing over", KnowledgeTopic.Handover),
        ["other"] = ("Other", KnowledgeTopic.Notes),
    };

    // Where the owner said it themselves: there is no "found on" to show.
    private static readonly HashSet<string> OwnOrigins = ["manual", "owner_input", "test_chat"];

    public bool Enabled => configuration.GetValue("OmniAi:Knowledge", false);

    public bool Available => Enabled;

    public bool CanEdit => false;

    public bool CanReadLinks => false;

    public async Task<KnowledgeSnapshot> ListAsync(CancellationToken cancellationToken = default)
    {
        List<Item> items = await omni.ListAllAsync<Item>($"{Root}/items", cancellationToken);
        List<Card> cards = await omni.ListAllAsync<Card>($"{Root}/review", cancellationToken);

        List<KnowledgeFact> facts = [.. items.Select(ToKnown)];
        facts.AddRange(cards.Where(card => card.Type == "found_detail").Select(ToConfirm));
        List<KnowledgeQuestion> questions = [.. cards.Where(card => card.Type is "customer_question" or "gap_ask").Select(ToQuestion)];
        List<KnowledgeConflict> conflicts = [.. cards.Where(card => card.Type == "disagreement").Select(ToConflict)];
        // A refused call is logged by the Omni-AI call logger (status, errorCode, traceId); this is what came back.
        logger.LogInformation(
            "knowledge.page.loaded Items={Items} Cards={Cards} ToConfirm={ToConfirm} Questions={Questions} Conflicts={Conflicts} UnknownCards={UnknownCards}",
            items.Count, cards.Count, facts.Count - items.Count, questions.Count, conflicts.Count,
            cards.Count(card => card.Type is not ("found_detail" or "customer_question" or "gap_ask" or "disagreement")));
        return new KnowledgeSnapshot(facts, questions, conflicts);
    }

    private static KnowledgeFact ToKnown(Item item)
    {
        (string label, KnowledgeTopic topic) = SectionOf(item.Section);
        return new KnowledgeFact(item.Id, topic, item.Text, FactState.Known, FoundOn(item.Sources),
            item.UpdatedAt ?? item.CreatedAt ?? DateTimeOffset.UtcNow, label);
    }

    // A found detail may have no section yet: its label stays empty until the AI files it.
    private static KnowledgeFact ToConfirm(Card card)
    {
        (string label, KnowledgeTopic topic) = card.Section is null ? ("", KnowledgeTopic.Notes) : SectionOf(card.Section);
        return new KnowledgeFact(card.Id, topic, card.Text ?? "", FactState.ToConfirm, FoundOn(card.Sources),
            card.CreatedAt ?? DateTimeOffset.UtcNow, label);
    }

    private static KnowledgeQuestion ToQuestion(Card card) =>
        new(card.Id, card.Question ?? "", card.AskedCount, card.LastAskedAt ?? card.CreatedAt ?? DateTimeOffset.UtcNow,
            card.Channels?.FirstOrDefault() ?? "");

    private static KnowledgeConflict ToConflict(Card card) =>
        new(card.Id, SectionOf(card.Section).Topic, card.Question ?? "",
            [.. (card.Options ?? []).Select(option => new KnowledgeClaim(option.Text, option.Source?.Label ?? ""))]);

    private static (string Label, KnowledgeTopic Topic) SectionOf(string? key) =>
        key is not null && Sections.TryGetValue(key, out (string Label, KnowledgeTopic Topic) section) ? section : Sections["other"];

    private static string? FoundOn(List<Source>? sources) =>
        sources?.FirstOrDefault() is { } source && !OwnOrigins.Contains(source.Origin ?? "") ? source.Label : null;

    public Task<IReadOnlyList<KnowledgeFact>> AddAsync(string text, KnowledgeTopic? topic = null, CancellationToken cancellationToken = default) => throw ReadOnly();
    public Task<KnowledgeFact> UpdateAsync(string id, string text, CancellationToken cancellationToken = default) => throw ReadOnly();
    public Task<KnowledgeFact> ConfirmAsync(string id, CancellationToken cancellationToken = default) => throw ReadOnly();
    public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => throw ReadOnly();
    public Task<KnowledgeFact> MoveAsync(string id, string label, CancellationToken cancellationToken = default) => throw ReadOnly();
    public Task RestoreAsync(KnowledgeFact fact, CancellationToken cancellationToken = default) => throw ReadOnly();
    public Task<IReadOnlyList<KnowledgeFact>> AnswerAsync(string questionId, string answer, CancellationToken cancellationToken = default) => throw ReadOnly();
    public Task DismissAsync(string questionId, CancellationToken cancellationToken = default) => throw ReadOnly();
    public Task<KnowledgeFact> ResolveAsync(string conflictId, string text, CancellationToken cancellationToken = default) => throw ReadOnly();
    public Task<IReadOnlyList<KnowledgeFact>> ReadWebsiteAsync(string url, CancellationToken cancellationToken = default) => throw ReadOnly();

    private static NotSupportedException ReadOnly() => new("Knowledge from the AI service is read-only for now.");

    private sealed record Source(string? Origin, string? Label);

    private sealed record Item(string Id, string? Section, string Text, List<Source>? Sources, DateTimeOffset? CreatedAt, DateTimeOffset? UpdatedAt);

    private sealed record Option(string Text, Source? Source);

    private sealed record Card(string Id, string Type, string? Section, string? Text, string? Question, List<Source>? Sources,
        List<Option>? Options, int AskedCount, List<string>? Channels, DateTimeOffset? LastAskedAt, DateTimeOffset? CreatedAt);
}
