using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Parrot.Web.Services;

namespace Parrot.Web.Features.Dashboard.Knowledge;

/// <summary>
/// The business's knowledge as the AI service keeps it (/api/v1/business-knowledge), as the signed-in person:
/// its published items are what the AI knows, its pending cards are the things to check. Only the Knowledge page
/// uses it. The owner can tell the AI something and rewrite a section; deciding the things to check comes later.
/// The service decides who may write, by the person's permissions. On when the web app's configuration says
/// "OmniAi": { "Knowledge": true } (or OmniAi__Knowledge=true).
/// </summary>
public sealed class OmniKnowledgeMemory(OmniAiClient omni, IConfiguration configuration, ILogger<OmniKnowledgeMemory> logger) : IKnowledgeMemory
{
    /// <summary>The service's answer when business knowledge is switched off on its side.</summary>
    public const string KnowledgeUnavailable = "KNOWLEDGE_UNAVAILABLE";

    private const string Root = "api/v1/business-knowledge";

    // Published items as last listed, so Undo can write back a statement an addition replaced: the service retires
    // a replaced item, and a retired item can't be restored or read again.
    private Dictionary<string, Item> _listed = [];

    // What each added item replaced, kept for its Undo.
    private readonly Dictionary<string, List<Item>> _replacedBy = [];

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

    public bool Enabled => IsOn(configuration, "OmniAi:Knowledge");

    /// <summary>
    /// A switch that is on only when it says "true". GetValue&lt;bool&gt; throws on an empty value
    /// (OmniAi__Knowledge= in a compose file), which would stop the app; missing, empty or mistyped is off.
    /// </summary>
    public static bool IsOn(IConfiguration configuration, string key) => bool.TryParse(configuration[key], out bool on) && on;

    public bool Available => Enabled;

    public bool CanEdit => true;

    public bool CanReview => false;

    public bool CanReadLinks => false;

    public async Task<KnowledgeSnapshot> ListAsync(CancellationToken cancellationToken = default)
    {
        List<Item> items = await omni.ListAllAsync<Item>($"{Root}/items", cancellationToken);
        List<Card> cards = await omni.ListAllAsync<Card>($"{Root}/review", cancellationToken);
        _listed = items.ToDictionary(item => item.Id);

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

    // ── Telling the AI something, and rewriting a section ─────────────────

    /// <summary>
    /// Each statement (a sentence or a line) is sent on its own: the service files everything in one request under
    /// one section, which it picks from the words when none is given, so "We deliver in Lagos. Pay by transfer."
    /// lands in two. A statement it already has comes back as nothing added.
    /// </summary>
    public async Task<IReadOnlyList<KnowledgeFact>> AddAsync(string text, KnowledgeTopic? topic = null, CancellationToken cancellationToken = default)
    {
        string? section = topic is { } given ? SectionFor(given) : null;
        List<KnowledgeFact> saved = [];
        int replacedCount = 0;
        IReadOnlyList<string> lines = Statements(text);
        foreach (string line in lines)
        {
            Added added = await WriteAsync("add", null,
                () => omni.SendAsync<Added>(HttpMethod.Post, $"{Root}/items", new NewItem(line, section), cancellationToken: cancellationToken));
            List<Item> replaced = [.. (added.Replaced ?? []).Select(id => _listed.GetValueOrDefault(id)).OfType<Item>()];
            foreach (Item item in added.Items) _replacedBy[item.Id] = replaced;
            replacedCount += added.Replaced?.Count ?? 0;
            saved.AddRange(added.Items.Select(ToKnown));
        }
        logger.LogInformation("knowledge.items.added Lines={Lines} Added={Added} Replaced={Replaced}", lines.Count, saved.Count, replacedCount);
        return saved;
    }

    /// <summary>
    /// Removes what was just added. A statement it replaced was retired, which the service can't restore, so it is
    /// written again, under the section it was in.
    /// </summary>
    public async Task UndoAddAsync(IReadOnlyList<KnowledgeFact> saved, CancellationToken cancellationToken = default)
    {
        int putBack = 0;
        foreach (KnowledgeFact fact in saved)
        {
            await DeleteAsync(fact.Id, cancellationToken);
            if (!_replacedBy.Remove(fact.Id, out List<Item>? replaced)) continue;
            foreach (Item item in replaced)
            {
                await WriteAsync("undo_add", null,
                    () => omni.SendAsync<Added>(HttpMethod.Post, $"{Root}/items", new NewItem(item.Text, item.Section), cancellationToken: cancellationToken));
                putBack++;
            }
        }
        logger.LogInformation("knowledge.add_undone Removed={Removed} PutBack={PutBack}", saved.Count, putBack);
    }

    /// <summary>Gone already counts as done: removing is what was wanted.</summary>
    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        try
        {
            await WriteAsync("delete", null, async () =>
            {
                await omni.SendNoContentAsync(HttpMethod.Delete, $"{Root}/items/{Uri.EscapeDataString(id)}", cancellationToken);
                return true;
            });
        }
        catch (ApiException refused) when (refused.ErrorCode == "ITEM_NOT_FOUND")
        {
            logger.LogInformation("knowledge.item.already_gone ItemId={ItemId}", id);
        }
    }

    public async Task<KnowledgeSectionDraft> OpenSectionAsync(string label, CancellationToken cancellationToken = default)
    {
        string key = KeyOf(label);
        Section section = await WriteAsync("open_section", label, () => omni.GetAsync<Section>($"{Root}/sections/{key}", cancellationToken));
        // The service sends up to 200 details: rewriting a longer section from here would remove the rest.
        if (section.ItemCount > section.Items.Count)
        {
            logger.LogWarning("knowledge.section.too_long Section={Section} Items={Items} Sent={Sent}", key, section.ItemCount, section.Items.Count);
            throw new KnowledgeSaveRefusedException($"{label} holds more details than can be edited here at once.");
        }
        return new KnowledgeSectionDraft(label,
            [.. section.Items.Select(item => new KnowledgeLine(item.Id, item.Text.Trim(), item.Kind is null or "knowledge"))],
            section.Version);
    }

    /// <summary>
    /// The section's statements are rewritten in one go, against the version that was opened, so a change someone
    /// else made in between is refused rather than overwritten. Facts and policies, which that rewrite leaves alone,
    /// stay as they are when their line is kept and are removed one by one when it is gone.
    /// </summary>
    public async Task SaveSectionAsync(KnowledgeSectionDraft opened, string text, CancellationToken cancellationToken = default)
    {
        string key = KeyOf(opened.Label);
        IReadOnlyList<string> lines = KnowledgeSectionDraft.Split(text);
        HashSet<string> others = [.. opened.Lines.Where(line => !line.Rewritable).Select(line => line.Text)];
        List<string> statements = [.. lines.Where(line => !others.Contains(line))];
        List<KnowledgeLine> removed = [.. opened.Lines.Where(line => !line.Rewritable && !lines.Contains(line.Text))];

        Rewritten? rewritten = null;
        if (statements.Count > 0)
        {
            rewritten = await WriteAsync("rewrite_section", opened.Label,
                () => omni.SendAsync<Rewritten>(HttpMethod.Put, $"{Root}/sections/{key}", new SectionText(string.Join("\n", statements)),
                    opened.Version, cancellationToken: cancellationToken));
        }
        else
        {
            // Every statement line taken out: the service takes no empty rewrite, so each goes on its own.
            removed.AddRange(opened.Lines.Where(line => line.Rewritable));
        }
        foreach (KnowledgeLine line in removed) await DeleteAsync(line.Id, cancellationToken);

        logger.LogInformation(
            "knowledge.section.saved Section={Section} FromVersion={FromVersion} ToVersion={ToVersion} Kept={Kept} Added={Added} Retired={Retired} Removed={Removed}",
            key, opened.Version, rewritten?.Version, rewritten?.Changes?.Kept?.Count, rewritten?.Changes?.Added?.Count,
            rewritten?.Changes?.Retired?.Count, removed.Count);
    }

    // The service's refusals, in words for the owner; anything else (a connection that dropped) is left to the page.
    private async Task<T> WriteAsync<T>(string action, string? label, Func<Task<T>> write)
    {
        try
        {
            return await write();
        }
        catch (ApiException refused) when (RefusalFor(refused, label) is { } words)
        {
            logger.LogWarning("knowledge.write_refused Action={Action} Status={Status} ErrorCode={ErrorCode}",
                action, (int?)refused.StatusCode, refused.ErrorCode);
            throw words;
        }
    }

    private static KnowledgeSaveRefusedException? RefusalFor(ApiException refused, string? label) => refused.ErrorCode switch
    {
        "VERSION_MISMATCH" => new($"Someone else changed {label ?? "this section"} while you were editing.", sectionChanged: true),
        "FORBIDDEN" or "USER_NOT_VALID" => new("You don't have permission to change what your AI knows. Ask the workspace owner."),
        "RATE_LIMITED" => new("That's a lot of changes in a minute. Wait a moment, then try again."),
        "VALIDATION" => new("We couldn't save that as written. Check it and try again."),
        "SECTION_NOT_FOUND" => new("That section can't be changed here. Reload the page and try again."),
        _ when refused.StatusCode == HttpStatusCode.ServiceUnavailable => new("Your AI's knowledge can't be changed right now. Try again in a minute."),
        _ => null,
    };

    private static IReadOnlyList<string> Statements(string text) =>
        [.. Regex.Split(text.Trim(), @"(?<=[.!?])\s+|\n+").Select(statement => statement.Trim()).Where(statement => statement.Length > 1).Distinct()];

    private static string KeyOf(string label) =>
        Sections.FirstOrDefault(section => section.Value.Label == label).Key ?? "other";

    private static string? SectionFor(KnowledgeTopic topic) => topic switch
    {
        KnowledgeTopic.Products => "products",
        KnowledgeTopic.Prices => "prices_offers",
        KnowledgeTopic.Delivery => "delivery",
        KnowledgeTopic.Payment => "payment",
        KnowledgeTopic.Hours => "hours_contact",
        KnowledgeTopic.Returns => "returns",
        KnowledgeTopic.Handover => "handover",
        _ => null,
    };

    // Deciding the things to check, and reading links, come in later phases (the page doesn't offer them yet).
    public Task<KnowledgeFact> UpdateAsync(string id, string text, CancellationToken cancellationToken = default) => throw NotYet();
    public Task<KnowledgeFact> ConfirmAsync(string id, CancellationToken cancellationToken = default) => throw NotYet();
    public Task<KnowledgeFact> MoveAsync(string id, string label, CancellationToken cancellationToken = default) => throw NotYet();
    public Task RestoreAsync(KnowledgeFact fact, CancellationToken cancellationToken = default) => throw NotYet();
    public Task<IReadOnlyList<KnowledgeFact>> AnswerAsync(string questionId, string answer, CancellationToken cancellationToken = default) => throw NotYet();
    public Task DismissAsync(string questionId, CancellationToken cancellationToken = default) => throw NotYet();
    public Task<KnowledgeFact> ResolveAsync(string conflictId, string text, CancellationToken cancellationToken = default) => throw NotYet();
    public Task<IReadOnlyList<KnowledgeFact>> ReadWebsiteAsync(string url, CancellationToken cancellationToken = default) => throw NotYet();

    private static NotSupportedException NotYet() => new("Not offered for the AI service's knowledge yet.");

    // The service's bodies are strict: a field it doesn't know is refused, so an empty section is left out.
    private sealed record NewItem(string Text, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Section = null);

    private sealed record SectionText(string Text);

    private sealed record Added(List<Item> Items, List<string>? Replaced);

    private sealed record Section(string Key, int Version, int ItemCount, List<Item> Items);

    private sealed record Changes(List<JsonElement>? Kept, List<JsonElement>? Added, List<JsonElement>? Retired);

    private sealed record Rewritten(int Version, Changes? Changes);

    private sealed record Source(string? Origin, string? Label);

    private sealed record Item(string Id, string? Section, string Text, List<Source>? Sources, DateTimeOffset? CreatedAt, DateTimeOffset? UpdatedAt,
        string? Kind = null);

    private sealed record Option(string Text, Source? Source);

    private sealed record Card(string Id, string Type, string? Section, string? Text, string? Question, List<Source>? Sources,
        List<Option>? Options, int AskedCount, List<string>? Channels, DateTimeOffset? LastAskedAt, DateTimeOffset? CreatedAt);
}
