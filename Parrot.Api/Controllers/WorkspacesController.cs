using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Parrot.Api.Middleware;
using Parrot.Api.Monitoring;
using Parrot.Api.Security;
using Parrot.Application.Auth;
using Parrot.Application.DTOs.Auth;
using Parrot.Application.DTOs.Workspaces;
using Parrot.Application.Profile;
using Parrot.Application.Teams;
using Parrot.Application.Workspaces;
using Parrot.Domain.Exceptions;

namespace Parrot.Api.Controllers;

/// <summary>
/// The workspaces the signed-in tenant owns: list them, make another, rename
/// one, and switch the session to one. A workspace someone else owns is a 404
/// on every route, the same answer as one that does not exist.
/// </summary>
[ApiController]
[Route("api/workspaces")]
[Authorize]
[Monitor(Area = "workspaces")]
public sealed class WorkspacesController(IWorkspaceService workspaces, IAuthService auth, IProfileService profile) : ControllerBase
{
    private string UserId => User.UserId()
        ?? throw new UnauthorizedException("Sign in to continue.");

    private string CurrentWorkspaceId => User.WorkspaceId()
        ?? throw new UnauthorizedException("Sign in to continue.");

    [HttpGet]
    [AllowWithoutWorkspace]
    [EndpointSummary("List the workspaces the signed-in tenant owns")]
    [EndpointDescription("Oldest first, so the first is the one the tenant started with. isCurrent marks the one this session works in.")]
    [ProducesResponseType(typeof(IReadOnlyList<WorkspaceResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IReadOnlyList<WorkspaceResponse>>> ListAsync() =>
        Ok(await workspaces.ListAsync(UserId, CurrentWorkspaceId, await profile.GetDefaultWorkspaceAsync(UserId)));

    [HttpPut("default")]
    [AllowWithoutWorkspace]
    [EndpointSummary("Choose where sign-in lands")]
    [EndpointDescription("A workspace id to always open it on sign-in, or null to resume where you left off (the default). It must be one you own or were given access to. If you later lose access to it, sign-in resumes instead.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetDefault([FromBody] SetDefaultWorkspaceRequest request)
    {
        await profile.SetDefaultWorkspaceAsync(UserId, string.IsNullOrWhiteSpace(request.WorkspaceId) ? null : request.WorkspaceId);
        return NoContent();
    }

    // Not [AllowWithoutWorkspace]: someone removed from the workspace gets the
    // same 403 here as everywhere else, which is the answer the catalog wants.
    [HttpGet("current/access")]
    [EndpointSummary("What this session may do in its workspace right now")]
    [EndpointDescription("Read from the person's grant on every call, so a change made since the token was issued shows here at once. The web app refreshes its menu from it, and the catalog service checks each request against it.")]
    [ProducesResponseType(typeof(SessionAccessResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    public ActionResult<SessionAccessResponse> CurrentAccess()
    {
        WorkspaceAccess access = HttpContext.WorkspaceAccess();
        return Ok(new SessionAccessResponse(
            access.Workspace.Id,
            access.Role,
            access.IsCustomRole,
            WorkspacePermissions.Effective(access),
            [.. WorkspaceChannels.All.Select(c => c.Key).Where(access.Channels.Contains)],
            WorkspacePermissions.CatalogScopes(access)));
    }

    [HttpPost]
    [AllowWithoutWorkspace]
    [EndpointSummary("Create a workspace")]
    [EndpointDescription("Makes a new, empty workspace owned by the signed-in tenant. The session stays where it is: switch to the new workspace with POST /api/workspaces/{id}/switch to start working in it.")]
    [ProducesResponseType(typeof(WorkspaceResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<WorkspaceResponse>> CreateAsync([FromBody] CreateWorkspaceRequest request)
    {
        WorkspaceResponse created = await workspaces.CreateAsync(UserId, request);
        return StatusCode(StatusCodes.Status201Created, created);
    }

    [HttpPut("{id}")]
    [AllowWithoutWorkspace] // the workspace named in the route is checked, owner only
    [EndpointSummary("Rename a workspace or change its logo")]
    [EndpointDescription("A partial update: leave a field out to leave it as it is. An empty logoUrl removes the logo.")]
    [ProducesResponseType(typeof(WorkspaceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WorkspaceResponse>> UpdateAsync(string id, [FromBody] UpdateWorkspaceRequest request)
    {
        WorkspaceResponse updated = await workspaces.UpdateAsync(UserId, id, request);
        return Ok(updated with { IsCurrent = id == CurrentWorkspaceId });
    }

    [HttpPost("{id}/switch")]
    [AllowWithoutWorkspace]
    [EndpointSummary("Switch the session to another workspace")]
    [EndpointDescription("Returns a new session token working in that workspace; use it in place of the old one. Everything the new token reads and writes — agents, channels, conversations, the catalog — belongs to that workspace. The next sign-in lands there too.")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AuthResponse>> SwitchAsync(string id) =>
        Ok(await auth.SwitchWorkspaceAsync(UserId, id));
}
