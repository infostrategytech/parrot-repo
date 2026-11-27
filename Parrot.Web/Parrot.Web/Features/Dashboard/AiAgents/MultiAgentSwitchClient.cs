using System.Net.Http.Headers;
using System.Net.Http.Json;
using Parrot.Application.Contracts.Agents;
using Parrot.Web.Features.Auth.State;
using Parrot.Web.Services;

namespace Parrot.Web.Features.Dashboard.AiAgents;

/// <summary>
/// The workspace's switch for agents sharing a number (temporary, for the live test). Agent settings, the Agents page
/// header and the Overview read it from the API each time, so turning it off takes effect at once.
/// </summary>
public sealed class MultiAgentSwitchClient(HttpClient http, AuthState auth)
{
    private const string Path = "api/multi-agent-switch";

    public Task<MultiAgentSwitchResponse> GetAsync(CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, null, cancellationToken);

    public Task<MultiAgentSwitchResponse> SetAsync(bool isOn, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, new SetMultiAgentSwitchRequest(isOn), cancellationToken);

    private async Task<MultiAgentSwitchResponse> SendAsync(HttpMethod method, SetMultiAgentSwitchRequest? body, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(method, Path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            auth.IsAuthenticated ? auth.CurrentUser!.Token : throw new InvalidOperationException("Sign in to continue."));
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        await response.EnsureApiSuccessAsync();
        return await response.Content.ReadFromJsonAsync<MultiAgentSwitchResponse>(cancellationToken)
            ?? throw new HttpRequestException("The server returned an empty response.");
    }
}
