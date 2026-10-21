using System.Security.Claims;
using Parrot.Application.Workspaces;

namespace Parrot.Api.Security;

/// <summary>
/// Who a session token is, and which workspace it works in. Kept apart on
/// purpose: the workspace owns the data (agents, channels, conversations),
/// the user is the person (their password, what they have read, what they wrote).
/// </summary>
public static class SessionClaims
{
    /// <summary>The signed-in person, or null on a principal without one.</summary>
    public static string? UserId(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");

    /// <summary>
    /// The workspace the session works in, and so the owner every read and
    /// write is scoped by. A token from before workspaces carries no claim;
    /// its user id is the tenant's first workspace, so it keeps working.
    /// </summary>
    public static string? WorkspaceId(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(WorkspaceClaims.WorkspaceId) ?? principal.UserId();
}
