using Parrot.Web.Services;
using System.Text.Json;
using Microsoft.JSInterop;
using Parrot.Web.Features.Auth.State;

namespace Parrot.Web.Features.Dashboard.Knowledge;

/// <summary>
/// The Knowledge page before the knowledge store exists (designer, 20 October 2026), so the team can see and try
/// it: the real page, starting empty (no sample data), with what the owner saves kept in this browser for their
/// account. Only the Knowledge page uses it: agents don't, and nothing is sent anywhere. There is nothing to
/// review (that needs the channels and the agents' conversations) and no reading links. Off unless the web
/// app's configuration says "Knowledge": { "Preview": true } (or Knowledge__Preview=true); it goes once the store
/// exists (knowledge-backend-requests 8-10, 20-22).
/// </summary>
public sealed class KnowledgePreview(IConfiguration configuration, IJSRuntime js, AuthState auth) : IKnowledgeMemory
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private List<KnowledgeFact>? _facts;

    public bool Enabled => configuration.GetValue("Knowledge:Preview", false);

    public bool Available => Enabled;

    public bool IsPreview => true;

    public bool CanReadLinks => false;

    private string Key => "omniai-knowledge-preview-" + (auth.CurrentUser?.UserId is { Length: > 0 } user ? user : "signed-out");

    // Read once from the browser. Before the page is interactive (prerendering) there is no browser to ask yet:
    // nothing, and nothing kept, so the interactive page asks again.
    private async Task<List<KnowledgeFact>> FactsAsync()
    {
        if (_facts is not null) return _facts;
        string? saved;
        try { saved = await js.InvokeAsync<string?>("localStorage.getItem", Key); }
        catch (InvalidOperationException) { return []; }
        catch (JSException) { saved = null; }
        try { _facts = saved is null ? [] : JsonSerializer.Deserialize<List<KnowledgeFact>>(saved, Json) ?? []; }
        catch (JsonException) { _facts = []; }
        return _facts;
    }

    private async Task KeepAsync()
    {
        if (_facts is null) return;
        try { await js.InvokeVoidAsync("localStorage.setItem", Key, JsonSerializer.Serialize(_facts, Json)); }
        catch (Exception exception) when (exception is InvalidOperationException or JSException) { }
    }

    public async Task<KnowledgeSnapshot> ListAsync(CancellationToken cancellationToken = default) =>
        new([.. await FactsAsync()], [], []);

    public async Task<IReadOnlyList<KnowledgeFact>> AddAsync(string text, KnowledgeTopic? topic = null, CancellationToken cancellationToken = default)
    {
        var facts = await FactsAsync();
        var saved = KnowledgeSorting.Sentences(text).Select(sentence => KnowledgeSorting.New(sentence, topic)).ToList();
        facts.AddRange(saved);
        await KeepAsync();
        return saved;
    }

    public async Task<KnowledgeFact> UpdateAsync(string id, string text, CancellationToken cancellationToken = default)
    {
        var facts = await FactsAsync();
        var index = Find(facts, id);
        var updated = KnowledgeSorting.Placed(facts[index]) with { Text = KnowledgeSorting.Tidy(text), State = FactState.Known, UpdatedAt = DateTimeOffset.UtcNow };
        facts[index] = updated;
        await KeepAsync();
        return updated;
    }

    public async Task<KnowledgeFact> ConfirmAsync(string id, CancellationToken cancellationToken = default)
    {
        var facts = await FactsAsync();
        var index = Find(facts, id);
        facts[index] = KnowledgeSorting.Placed(facts[index]) with { State = FactState.Known, UpdatedAt = DateTimeOffset.UtcNow };
        await KeepAsync();
        return facts[index];
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        (await FactsAsync()).RemoveAll(fact => fact.Id == id);
        await KeepAsync();
    }

    public async Task<KnowledgeFact> MoveAsync(string id, string label, CancellationToken cancellationToken = default)
    {
        var facts = await FactsAsync();
        var index = Find(facts, id);
        var heading = label.Trim();
        heading = facts.Select(fact => fact.Label).FirstOrDefault(existing => string.Equals(existing, heading, StringComparison.CurrentCultureIgnoreCase)) ?? heading;
        var moved = facts[index] with { Label = heading, UpdatedAt = DateTimeOffset.UtcNow };
        facts[index] = moved;
        await KeepAsync();
        return moved;
    }

    public async Task RestoreAsync(KnowledgeFact fact, CancellationToken cancellationToken = default)
    {
        var facts = await FactsAsync();
        if (facts.All(existing => existing.Id != fact.Id)) facts.Add(fact);
        await KeepAsync();
    }

    // Nothing to review in the preview: no questions customers asked, no disagreements, no links read.
    public Task<IReadOnlyList<KnowledgeFact>> AnswerAsync(string questionId, string answer, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DismissAsync(string questionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<KnowledgeFact> ResolveAsync(string conflictId, string text, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<KnowledgeFact>> ReadWebsiteAsync(string url, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    private static int Find(List<KnowledgeFact> facts, string id)
    {
        var index = facts.FindIndex(fact => fact.Id == id);
        if (index < 0) throw new HttpRequestException("No such fact.", null, System.Net.HttpStatusCode.NotFound);
        return index;
    }
}
