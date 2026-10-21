using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Parrot.Api.Middleware;
using Parrot.Api.Monitoring;
using Parrot.Api.Security;
using Parrot.Application.Auth;
using Parrot.Application.DTOs.Auth;
using Parrot.Application.DTOs.Workspaces;
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
public sealed class WorkspacesController(IWorkspaceService workspaces, IAuthService auth) : ControllerBase
{
    private string UserId => User.UserId()
        ?? throw new UnauthorizedException("Sign in to continue.");

    private string CurrentWorkspaceId => User.WorkspaceId()
        ?? throw new UnauthorizedException("Sign in to continue.");

    [HttpGet]
    [EndpointSummary("List the workspaces the signed-in tenant owns")]
    [EndpointDescription("Oldest first, so the first is the one the tenant started with. isCurrent marks the one this session works in.")]
    [ProducesResponseType(typeof(IReadOnlyList<WorkspaceResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IReadOnlyList<WorkspaceResponse>>> ListAsync() =>
        Ok(await workspaces.ListAsync(UserId, CurrentWorkspaceId));

    [HttpPost]
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
    [EndpointSummary("Switch the session to another workspace")]
    [EndpointDescription("Returns a new session token working in that workspace; use it in place of the old one. Everything the new token reads and writes — agents, channels, conversations, the catalog — belongs to that workspace. The next sign-in lands there too.")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AuthResponse>> SwitchAsync(string id) =>
        Ok(await auth.SwitchWorkspaceAsync(UserId, id));
}
