using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Filters;
using Parrot.Application.Auth;
using Parrot.Application.Teams;
using Parrot.Domain.Exceptions;

namespace Parrot.Api.Security;

/// <summary>
/// Runs before every signed-in request: works out what the person may do in
/// the workspace their token names, and refuses the request when that is
/// nothing, so a member who was removed loses access at once rather than
/// when their token expires. The answer is kept on the request for
/// <see cref="RequirePermissionAttribute"/> and for controllers that filter
/// by channel.
/// </summary>
public sealed class WorkspaceAccessFilter(IWorkspaceAccessService access) : IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        IList<object> metadata = context.ActionDescriptor.EndpointMetadata;
        if (metadata.OfType<IAllowAnonymous>().Any() || metadata.OfType<AllowWithoutWorkspaceAttribute>().Any())
        {
            return;
        }

        System.Security.Claims.ClaimsPrincipal user = context.HttpContext.User;
        // Unauthenticated requests are left to [Authorize]; service tokens act
        // for a tenant, not as a person in a workspace. A service token is
        // recognised by its svc claim (ServiceScopes.ServiceClaim) alone. One
        // minted without svc is treated as a person whose user id is its sub,
        // the workspace id: that matches the owner in a tenant's first
        // workspace and nobody in any other, so it would be refused there.
        // Every service minting tokens for this API must set svc.
        if (user.Identity?.IsAuthenticated != true || user.HasClaim(c => c.Type == ServiceScopes.ServiceClaim))
        {
            return;
        }

        string? userId = user.UserId();
        string? workspaceId = user.WorkspaceId();
        if (userId is null || workspaceId is null)
        {
            return;
        }

        WorkspaceAccess? found = await access.GetAsync(userId, workspaceId);
        if (found is null)
        {
            throw new ForbiddenException("You no longer have access to this workspace. Switch to another one or sign in again.");
        }

        context.HttpContext.Items[typeof(WorkspaceAccess)] = found;
    }
}

/// <summary>For routes that must work whatever the session's workspace: switching away from one, accepting an invite, the person's own account.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AllowWithoutWorkspaceAttribute : Attribute;

/// <summary>Refuses the request unless the person may do this in the session's workspace. The owner may do everything.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequirePermissionAttribute(string permission) : Attribute, IAuthorizationFilter
{
    public string Permission { get; } = permission;

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        WorkspaceAccess access = context.HttpContext.WorkspaceAccess();
        if (!access.Can(Permission))
        {
            throw new ForbiddenException("Your role in this workspace does not allow this. Ask the owner for access.");
        }
    }
}

/// <summary>Refuses the request unless the person may do at least one of these. The owner may do everything.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireAnyPermissionAttribute(params string[] permissions) : Attribute, IAuthorizationFilter
{
    public IReadOnlyList<string> Permissions { get; } = permissions;

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        WorkspaceAccess access = context.HttpContext.WorkspaceAccess();
        if (!Permissions.Any(access.Can))
        {
            throw new ForbiddenException("Your role in this workspace does not allow this. Ask the owner for access.");
        }
    }
}

/// <summary>
/// On a channel's setup routes: refuses the request unless the person's grant
/// reaches that channel. Applies to the routes that need a permission, which
/// are the ones that change a setup (TikTok's authorize is a GET among them);
/// reading a setup stays open to every member, since the dashboard shows which
/// channels are connected.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class RequireChannelAttribute(string channel) : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        if (!context.ActionDescriptor.EndpointMetadata.OfType<RequirePermissionAttribute>().Any())
        {
            return;
        }

        WorkspaceAccess access = context.HttpContext.WorkspaceAccess();
        if (access.Channels.Count > 0 && !access.Channels.Contains(channel))
        {
            throw new ForbiddenException("Your access in this workspace does not include this channel.");
        }
    }
}

public static class WorkspaceAccessHttpContext
{
    /// <summary>What the person may do in the session's workspace, as <see cref="WorkspaceAccessFilter"/> found it.</summary>
    public static WorkspaceAccess WorkspaceAccess(this HttpContext context) =>
        context.Items[typeof(WorkspaceAccess)] as WorkspaceAccess
            ?? throw new UnauthorizedException("Sign in to continue.");

    /// <summary>Not found, the same answer as a connection that does not exist, unless the person's channels reach it.</summary>
    public static void RequireReach(this HttpContext context, Guid connectionId)
    {
        if (!context.WorkspaceAccess().CanReach(connectionId))
        {
            throw new NotFoundException("Conversation not found.");
        }
    }
}
