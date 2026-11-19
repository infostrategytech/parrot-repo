using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Parrot.Application.Agents;
using Parrot.Web.Components.Pages.Dashboard.AiAgents;
using Parrot.Web.Features.Auth.State;
using Parrot.Web.Features.Dashboard.AiAgents.State;
using Parrot.Web.Services;

namespace Parrot.Web.Features.Dashboard.AiAgents;

/// <summary>
/// The AI Operations figures from the API's activity store, held as one
/// snapshot per circuit so the screens can read them without waiting. A
/// figure asked for the first time (an agent, on one channel or all) is
/// loaded in the background and <see cref="Changed"/> says when it is there;
/// <see cref="RefreshAsync"/> reads every figure asked for so far again, and
/// is called whenever an agent reports something.
/// </summary>
public sealed class ApiAiOperationsFeed(HttpClient http, AuthState auth, ILogger<ApiAiOperationsFeed> logger) : IAiOperationsFeed
{
    private const string AllChannels = "*";

    private readonly ConcurrentDictionary<string, AgentDay> _days = new();
    private readonly ConcurrentDictionary<string, byte> _loading = new();
    private IReadOnlyList<WaitingQuestion> _waiting = [];
    private volatile bool _loaded;

    public bool Available => _loaded;

    public event Action? Changed;

    public AgentToday Today(AiAgent agent, string? channel = null) => Day(agent, channel)?.Today ?? AgentToday.None;

    public IReadOnlyList<int> Week(AiAgent agent, string? channel = null) => Day(agent, channel)?.Week ?? [];

    public IReadOnlyList<RecentConversation> Recent(AiAgent agent, string? channel = null) => Day(agent, channel)?.Recent ?? [];

    public IReadOnlyList<WaitingQuestion> Waiting(IReadOnlyList<AiAgent> agents) => _waiting;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!auth.IsAuthenticated) return;
        try
        {
            var loads = _days.Keys.Select(key => LoadDayAsync(key, cancellationToken)).ToList();
            loads.Add(LoadWaitingAsync(cancellationToken));
            await Task.WhenAll(loads);
            _loaded = true;
        }
        catch (Exception exception) when (AiAgentsState.IsFailure(exception))
        {
            // The last figures stay on screen; the next report or visit reads again.
            logger.LogWarning(exception, "ai_operations.refresh_failed");
        }

        Changed?.Invoke();
    }

    private AgentDay? Day(AiAgent agent, string? channel)
    {
        var key = Key(agent.Id, channel);
        if (_days.TryGetValue(key, out var day)) return day;
        if (_loading.TryAdd(key, 0)) _ = LoadLaterAsync(key);
        return null;
    }

    // First time asked: load it, then tell the screens. A failed load leaves
    // zeros under the key, so the screen settles rather than asking again on
    // every draw; the next refresh reads it again.
    private async Task LoadLaterAsync(string key)
    {
        try
        {
            await LoadDayAsync(key, CancellationToken.None);
        }
        catch (Exception exception) when (AiAgentsState.IsFailure(exception) || exception is InvalidOperationException)
        {
            logger.LogWarning(exception, "ai_operations.load_failed Key={Key}", key);
            _days.TryAdd(key, AgentDay.None);
        }
        finally
        {
            _loading.TryRemove(key, out _);
        }

        Changed?.Invoke();
    }

    private async Task LoadDayAsync(string key, CancellationToken cancellationToken)
    {
        var (agentId, channel) = Split(key);
        var query = $"agentId={Uri.EscapeDataString(agentId)}" + (channel is null ? "" : $"&channel={Uri.EscapeDataString(channel)}");
        var today = GetAsync<AgentTodayResponse>($"api/ai-operations/today?{query}", cancellationToken);
        var week = GetAsync<List<int>>($"api/ai-operations/week?{query}", cancellationToken);
        var recent = GetAsync<List<RecentConversationResponse>>($"api/ai-operations/recent?{query}&limit=20", cancellationToken);
        await Task.WhenAll(today, week, recent);

        var t = await today;
        _days[key] = new AgentDay(
            new AgentToday(t.Conversations, t.AnsweredAlone, t.PassedToTeam, t.TasksCreated, t.TeamReplyMinutes),
            await week,
            (await recent).Select(r => new RecentConversation(
                r.Customer, r.Channel, Enum.TryParse<ConversationOutcome>(r.Outcome, true, out var outcome) ? outcome : ConversationOutcome.PassedToTeam,
                r.Task, r.MinutesAgo)).ToList());
    }

    private async Task LoadWaitingAsync(CancellationToken cancellationToken)
    {
        // Additive frontend read: older API responses still work; missing identities lead to the queue,
        // never a guessed customer/agent link. The backend must project the same pending Inbox records.
        var waiting = await GetAsync<List<InboxWaitingItemResponse>>("api/ai-operations/waiting", cancellationToken);
        _waiting = waiting.Select(w => new WaitingQuestion(w.AgentName, w.Customer, w.Question)
        { AgentId = w.AgentId, ConversationId = w.ConversationId, MessageId = w.MessageId }).ToList();
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        var token = auth.IsAuthenticated ? auth.CurrentUser!.Token : throw new InvalidOperationException("Sign in to see your agents' day.");
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, cancellationToken);
        await response.EnsureApiSuccessAsync();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
            ?? throw new HttpRequestException("The server returned an empty response.");
    }

    private static string Key(string agentId, string? channel) => $"{agentId}|{(string.IsNullOrWhiteSpace(channel) ? AllChannels : channel.ToLowerInvariant())}";

    private static (string AgentId, string? Channel) Split(string key)
    {
        var at = key.LastIndexOf('|');
        var channel = key[(at + 1)..];
        return (key[..at], channel == AllChannels ? null : channel);
    }

    private sealed record AgentDay(AgentToday Today, IReadOnlyList<int> Week, IReadOnlyList<RecentConversation> Recent)
    {
        public static readonly AgentDay None = new(AgentToday.None, [], []);
    }

    private sealed record InboxWaitingItemResponse(string AgentName, string Customer, string Question,
        string? AgentId = null, string? ConversationId = null, string? MessageId = null);
}
