using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Parrot.Application.DTOs.Agents;
using Parrot.Web.Components.Pages.Dashboard.AiAgents;
using Parrot.Web.Features.Auth.State;
using Parrot.Web.Services;

namespace Parrot.Web.Features.Dashboard.AiAgents;

/// <summary>
/// The agents as the API keeps them, read and written with the session's
/// token. Failures throw the ApiException the screens already treat as
/// "try again" (an HttpRequestException), so nothing looks saved that is not.
/// </summary>
public sealed class ApiAgentStore(HttpClient http, AuthState auth) : IAgentStore
{
    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<AiAgent>> ListAsync(CancellationToken cancellationToken = default)
    {
        var agents = await RequestAsync<List<AgentResponse>>(HttpMethod.Get, "api/agent", null, cancellationToken) ?? [];
        // The API lists newest first; the screens want newest last.
        return agents.Select(AgentMapping.ToAgent).Reverse().ToList();
    }

    public async Task<AiAgent> CreateAsync(AiAgent agent, CancellationToken cancellationToken = default)
    {
        var saved = await RequestAsync<AgentResponse>(HttpMethod.Post, "api/agent", AgentMapping.ToInfo(agent), cancellationToken)
            ?? throw new HttpRequestException("The server returned an empty response.");
        var result = AgentMapping.ToAgent(saved);
        // What the API does not keep yet stays as it was entered.
        result.Playbooks = agent.Playbooks;
        result.Settings.NoteFiles = [.. agent.Settings.NoteFiles];
        return result;
    }

    public Task SaveAsync(AiAgent agent, CancellationToken cancellationToken = default) =>
        RequestAsync<AgentResponse>(HttpMethod.Put, $"api/agent/{Uri.EscapeDataString(agent.Id)}", AgentMapping.ToInfo(agent), cancellationToken);

    public Task SetLiveAsync(string agentId, bool on, CancellationToken cancellationToken = default) =>
        RequestAsync<AgentResponse>(HttpMethod.Patch, $"api/agent/{Uri.EscapeDataString(agentId)}/live", new AgentLiveRequest(on), cancellationToken);

    public Task DeleteAsync(string agentId, CancellationToken cancellationToken = default) =>
        RequestAsync<object>(HttpMethod.Delete, $"api/agent/{Uri.EscapeDataString(agentId)}", null, cancellationToken);

    private async Task<T?> RequestAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var token = auth.IsAuthenticated ? auth.CurrentUser!.Token : throw new InvalidOperationException("Sign in to manage agents.");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: WriteOptions);
        using var response = await http.SendAsync(request, cancellationToken);
        await response.EnsureApiSuccessAsync();
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0) return default;
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);
    }
}
