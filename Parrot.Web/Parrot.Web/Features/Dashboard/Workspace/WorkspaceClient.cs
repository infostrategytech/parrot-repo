using System.Net.Http.Headers;
using System.Net.Http.Json;
using Parrot.Web.Features.Auth.Models;
using Parrot.Web.Features.Auth.State;
using Parrot.Web.Services;

namespace Parrot.Web.Features.Dashboard.Workspace;

/// <summary>One workspace as GET /api/workspaces lists it.</summary>
public sealed record WorkspaceSummary(string Id, string? Name, string? LogoUrl, bool IsCurrent);

/// <summary>
/// The workspaces the signed-in owner has: list them, make one, rename the
/// current one, and switch the session to another. Switching hands back a new
/// session token, which is saved and put in place of the old one here.
/// Failures throw an <see cref="ApiException"/> carrying the API's message.
/// </summary>
public sealed class WorkspaceClient(HttpClient http, AuthState authState, TokenStorageService tokenStorage)
{
    public async Task<IReadOnlyList<WorkspaceSummary>> ListAsync()
    {
        using HttpRequestMessage request = Authorized(HttpMethod.Get, "api/workspaces");
        using HttpResponseMessage response = await http.SendAsync(request);
        return await ReadAsync<List<WorkspaceSummary>>(response);
    }

    /// <summary>Makes a workspace. The session stays where it is until <see cref="SwitchAsync"/>.</summary>
    public async Task<WorkspaceSummary> CreateAsync(string name, string? logoUrl)
    {
        using HttpRequestMessage request = Authorized(HttpMethod.Post, "api/workspaces");
        request.Content = JsonContent.Create(new { name, logoUrl });
        using HttpResponseMessage response = await http.SendAsync(request);
        return await ReadAsync<WorkspaceSummary>(response);
    }

    /// <summary>
    /// Renames the current workspace or changes its logo. A null field is left
    /// as it is; an empty logo URL removes it. The session is brought up to date
    /// so the switcher and the dashboard show the change straight away.
    /// </summary>
    public async Task<WorkspaceSummary> UpdateCurrentAsync(string? name, string? logoUrl)
    {
        AuthResponse current = authState.CurrentUser
            ?? throw new InvalidOperationException("Workspaces are only reachable while signed in.");

        using HttpRequestMessage request = Authorized(HttpMethod.Put, $"api/workspaces/{Uri.EscapeDataString(current.CurrentWorkspaceId)}");
        request.Content = JsonContent.Create(new { name, logoUrl });
        using HttpResponseMessage response = await http.SendAsync(request);
        WorkspaceSummary updated = await ReadAsync<WorkspaceSummary>(response);

        await KeepSessionAsync(new AuthResponse
        {
            Token = current.Token,
            ExpiresAt = current.ExpiresAt,
            UserId = current.UserId,
            Email = current.Email,
            WorkspaceId = current.CurrentWorkspaceId,
            BusinessName = updated.Name,
            LogoUrl = updated.LogoUrl,
        });

        return updated;
    }

    /// <summary>
    /// Moves the session to another workspace: a new token, saved before it is
    /// used, so a reload lands in the same workspace.
    /// </summary>
    public async Task SwitchAsync(string workspaceId)
    {
        using HttpRequestMessage request = Authorized(HttpMethod.Post, $"api/workspaces/{Uri.EscapeDataString(workspaceId)}/switch");
        using HttpResponseMessage response = await http.SendAsync(request);
        AuthResponse session = await ReadAsync<AuthResponse>(response);

        await KeepSessionAsync(session);
    }

    private async Task KeepSessionAsync(AuthResponse session)
    {
        await tokenStorage.SaveAsync(session);
        authState.SetUser(session);
    }

    private HttpRequestMessage Authorized(HttpMethod method, string path)
    {
        string token = authState.CurrentUser?.Token
            ?? throw new InvalidOperationException("Workspaces are only reachable while signed in.");

        HttpRequestMessage request = new(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        await response.EnsureApiSuccessAsync();
        return await response.Content.ReadFromJsonAsync<T>()
            ?? throw new HttpRequestException("The server returned an empty response.");
    }
}
