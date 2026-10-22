using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Parrot.Web.Features.Auth.Models;
using Parrot.Web.Features.Auth.State;
using Parrot.Web.Services;

namespace Parrot.Web.Features.Dashboard.Team;

/// <summary>
/// What a person may do in one workspace, or, with no workspace id, in all of
/// the owner's workspaces: a role, and the channels they see. A workspace's own
/// grant is used there instead of the all-workspaces one. No channels is every channel.
/// </summary>
public sealed record TeamGrant(string? WorkspaceId, string RoleId, string RoleName, bool IsCustomRole, IReadOnlyList<string> Channels);

/// <summary>A grant as the invite form sends it: the role decides the permissions.</summary>
public sealed record GrantRequest(string? WorkspaceId, string RoleId, IReadOnlyList<string> Channels);

public sealed record TeamMember(string UserId, string? FullName, string Email, bool IsOwner, IReadOnlyList<TeamGrant> Grants, DateTime JoinedAt);

public sealed record TeamInvite(Guid Id, string FullName, string Email, IReadOnlyList<TeamGrant> Grants, DateTime InvitedAt, DateTime ExpiresAt);

public sealed record TeamWorkspace(string Id, string? Name, string? LogoUrl);

public sealed record TeamSnapshot(IReadOnlyList<TeamMember> Members, IReadOnlyList<TeamInvite> Invites, IReadOnlyList<TeamWorkspace> Workspaces);

public sealed record TeamOption(string Key, string Label);

/// <summary>A built-in role (read-only, the same everywhere) or one of the owner's own (IsCustom).</summary>
public sealed record TeamRole(string Id, string Name, bool IsCustom, IReadOnlyList<string> Permissions, int Holders);

/// <summary>The roles, permissions and channels the invite form offers, as the API lists them.</summary>
public sealed record TeamCatalog(IReadOnlyList<TeamRole> Roles, IReadOnlyList<TeamOption> Permissions, IReadOnlyList<TeamOption> Channels);

/// <summary>What an invite link is for, readable before signing in. HasAccount says whether to sign in or set a password.</summary>
public sealed record InvitePreview(string TeamName, string? TeamLogoUrl, string InvitedBy, string Email, IReadOnlyList<string> Workspaces,
    DateTime ExpiresAt, string Status, bool HasAccount, string FullName);

/// <summary>
/// The owner's team through the API, and accepting an invite. Failures throw
/// an <see cref="ApiException"/> carrying the API's message.
/// </summary>
public sealed class TeamClient(HttpClient http, AuthState authState, TokenStorageService tokenStorage)
{
    public async Task<TeamCatalog> GetCatalogAsync() => await SendAsync<TeamCatalog>(HttpMethod.Get, "api/team/roles");

    public async Task<TeamSnapshot> GetTeamAsync() => await SendAsync<TeamSnapshot>(HttpMethod.Get, "api/team");

    public async Task<TeamRole> CreateRoleAsync(string name, IReadOnlyList<string> permissions) =>
        await SendAsync<TeamRole>(HttpMethod.Post, "api/team/roles", new { name, permissions });

    /// <summary>A copy of any role, built-in included, as a role of the owner's own.</summary>
    public async Task<TeamRole> DuplicateRoleAsync(string roleId) =>
        await SendAsync<TeamRole>(HttpMethod.Post, $"api/team/roles/{Uri.EscapeDataString(roleId)}/duplicate");

    public async Task<TeamRole> UpdateRoleAsync(string roleId, string name, IReadOnlyList<string> permissions) =>
        await SendAsync<TeamRole>(HttpMethod.Put, $"api/team/roles/{Uri.EscapeDataString(roleId)}", new { name, permissions });

    public async Task DeleteRoleAsync(string roleId) => await SendAsync(HttpMethod.Delete, $"api/team/roles/{Uri.EscapeDataString(roleId)}");

    public async Task<TeamInvite> InviteAsync(string fullName, string email, IReadOnlyList<GrantRequest> grants) =>
        await SendAsync<TeamInvite>(HttpMethod.Post, "api/team/invites", new { fullName, email, grants });

    public async Task<TeamInvite> ResendAsync(Guid inviteId) =>
        await SendAsync<TeamInvite>(HttpMethod.Post, $"api/team/invites/{inviteId}/resend");

    public async Task RevokeAsync(Guid inviteId) => await SendAsync(HttpMethod.Delete, $"api/team/invites/{inviteId}");

    public async Task<TeamMember> UpdateMemberAsync(string userId, IReadOnlyList<GrantRequest> grants) =>
        await SendAsync<TeamMember>(HttpMethod.Put, $"api/team/members/{Uri.EscapeDataString(userId)}", new { grants });

    public async Task RemoveMemberAsync(string userId) =>
        await SendAsync(HttpMethod.Delete, $"api/team/members/{Uri.EscapeDataString(userId)}");

    /// <summary>Needs no session: the link's page reads it before anyone signs in.</summary>
    public async Task<InvitePreview> PreviewAsync(string token)
    {
        using HttpResponseMessage response = await http.GetAsync($"api/invites/{Uri.EscapeDataString(token)}");
        return await ReadAsync<InvitePreview>(response);
    }

    /// <summary>
    /// For an invited address with no account: makes the one login it will use
    /// everywhere, joins the team and starts the session there.
    /// </summary>
    public async Task SignUpAsync(string token, string firstName, string? lastName, string password)
    {
        using HttpResponseMessage response = await http.PostAsJsonAsync(
            $"api/invites/{Uri.EscapeDataString(token)}/sign-up", new { firstName, lastName, password });
        AuthResponse session = await ReadAsync<AuthResponse>(response);
        await tokenStorage.SaveAsync(session);
        authState.SetUser(session);
    }

    /// <summary>Joins the team and moves the session into it: a new token, saved before it is used.</summary>
    public async Task AcceptAsync(string token)
    {
        AuthResponse session = await SendAsync<AuthResponse>(HttpMethod.Post, $"api/invites/{Uri.EscapeDataString(token)}/accept");
        await tokenStorage.SaveAsync(session);
        authState.SetUser(session);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body = null)
    {
        using HttpResponseMessage response = await http.SendAsync(Request(method, path, body));
        return await ReadAsync<T>(response);
    }

    private async Task SendAsync(HttpMethod method, string path)
    {
        using HttpResponseMessage response = await http.SendAsync(Request(method, path, null));
        await response.EnsureApiSuccessAsync();
    }

    private HttpRequestMessage Request(HttpMethod method, string path, object? body)
    {
        string token = authState.CurrentUser?.Token
            ?? throw new InvalidOperationException("The team is only reachable while signed in.");

        HttpRequestMessage request = new(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        await response.EnsureApiSuccessAsync();
        return await response.Content.ReadFromJsonAsync<T>()
            ?? throw new HttpRequestException("The server returned an empty response.");
    }
}

/// <summary>
/// An invite link opened before signing in. Kept in the browser across sign-in,
/// sign-up, email verification and Google or Facebook sign-in, so the dashboard
/// can bring the person back to the link to accept it.
/// </summary>
public sealed class PendingInvite(ProtectedLocalStorage storage)
{
    private const string Key = "pending-invite";

    public async Task RememberAsync(string token) => await storage.SetAsync(Key, token);

    /// <summary>The token, once: reading it forgets it, so the dashboard does not keep sending the person back.</summary>
    public async Task<string?> TakeAsync()
    {
        try
        {
            ProtectedBrowserStorageResult<string> found = await storage.GetAsync<string>(Key);
            if (!found.Success || string.IsNullOrEmpty(found.Value)) return null;
            await storage.DeleteAsync(Key);
            return found.Value;
        }
        catch
        {
            // Storage written by an older key ring, or unreadable: there is no invite to resume.
            return null;
        }
    }
}
