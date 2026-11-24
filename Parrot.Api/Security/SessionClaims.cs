using System.Net.Http.Headers;
using System.Security.Claims;
using Parrot.Application.Workspaces;
using Parrot.Domain.Exceptions;

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

    /// <summary>
    /// The session token exactly as the person sent it, for a service that acts as them (the AI service's waiting
    /// list records who closed an item). Passed on to that service only, never logged or returned.
    /// </summary>
    public static string SessionToken(this HttpContext context) =>
        AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out AuthenticationHeaderValue? header)
            && header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(header.Parameter)
            ? header.Parameter
            : throw new UnauthorizedException("Sign in to continue.");
}
