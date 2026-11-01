using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Parrot.Api.Middleware;
using Parrot.Api.Monitoring;
using Parrot.Api.Security;
using Parrot.Api.Utils;
using Parrot.Application.Auth;
using Parrot.Application.DTOs.Auth;
using Parrot.Application.DTOs.Teams;
using Parrot.Application.Teams;
using Parrot.Domain.Exceptions;

namespace Parrot.Api.Controllers;

/// <summary>
/// The signed-in owner's team, from a session in any of their workspaces: the
/// people in it, what each may do in which workspaces and on which channels,
/// and the invitations still waiting. Only the owner can use these routes.
/// </summary>
[ApiController]
[Route("api/team")]
[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Monitor(Area = "team")]
public sealed class TeamController(ITeamService team) : ControllerBase
{
    private string UserId => User.UserId()
        ?? throw new UnauthorizedException("Sign in to continue.");

    private WorkspaceAccess Access => HttpContext.WorkspaceAccess();

    [HttpGet("roles")]
    [RequirePermission(WorkspacePermissions.TeamManage)]
    [EndpointSummary("The roles, permissions and channels a grant can use")]
    [EndpointDescription("Built-in roles first (read-only, the same in every business), then the owner's own, each with how many people hold it.")]
    [ProducesResponseType(typeof(TeamRolesResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<TeamRolesResponse>> Roles() => Ok(await team.GetRolesAsync(Access));

    [HttpPost("roles")]
    [RequirePermission(WorkspacePermissions.TeamManage)]
    [EndpointSummary("Create a role of the owner's own")]
    [ProducesResponseType(typeof(RoleResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RoleResponse>> CreateRole([FromBody] SaveRoleRequest request) =>
        StatusCode(StatusCodes.Status201Created, await team.CreateRoleAsync(Access, request));

    [HttpPost("roles/{id}/duplicate")]
    [RequirePermission(WorkspacePermissions.TeamManage)]
    [EndpointSummary("Copy any role into a new role of the owner's own")]
    [EndpointDescription("Works on built-in roles too: the way to start from one and change it. The copy is named \"Name (copy)\" and can be renamed, even to the original's name.")]
    [ProducesResponseType(typeof(RoleResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RoleResponse>> DuplicateRole(string id) =>
        StatusCode(StatusCodes.Status201Created, await team.DuplicateRoleAsync(Access, id));

    [HttpPut("roles/{id}")]
    [RequirePermission(WorkspacePermissions.TeamManage)]
    [EndpointSummary("Change one of the owner's own roles")]
    [EndpointDescription("Everyone holding it gets the change on their next request. Built-in roles answer 403.")]
    [ProducesResponseType(typeof(RoleResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RoleResponse>> UpdateRole(string id, [FromBody] SaveRoleRequest request) =>
        Ok(await team.UpdateRoleAsync(Access, id, request));

    [HttpDelete("roles/{id}")]
    [RequirePermission(WorkspacePermissions.TeamManage)]
    [EndpointSummary("Delete one of the owner's own roles")]
    [EndpointDescription("Refused while anyone holds it or a pending invite names it. Built-in roles answer 403.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteRole(string id)
    {
        await team.DeleteRoleAsync(Access, id);
        return NoContent();
    }

    [HttpGet]
    [RequirePermission(WorkspacePermissions.TeamManage)]
    [EndpointSummary("The owner's team")]
    [EndpointDescription("The owner first, then members with their grants, then pending invitations, and the owner's workspaces for choosing where a grant applies.")]
    [ProducesResponseType(typeof(TeamResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<TeamResponse>> Get() => Ok(await team.GetTeamAsync(Access));

    [HttpPost("invites")]
    [RequirePermission(WorkspacePermissions.TeamManage)]
    [EnableRateLimiting(RateLimitPolicies.TeamInvite)]
    [EndpointSummary("Invite someone to the team")]
    [EndpointDescription("Emails a link that works for 7 days. The person signs in or signs up with that address to accept it, and gets the grants listed here.")]
    [ProducesResponseType(typeof(TeamInviteResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<TeamInviteResponse>> Invite([FromBody] CreateInviteRequest request) =>
        StatusCode(StatusCodes.Status201Created, await team.InviteAsync(Access, UserId, request));

    [HttpPost("invites/{id:guid}/resend")]
    [RequirePermission(WorkspacePermissions.TeamManage)]
    [EnableRateLimiting(RateLimitPolicies.TeamInvite)]
    [EndpointSummary("Send an invite again, with a new link")]
    [ProducesResponseType(typeof(TeamInviteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TeamInviteResponse>> Resend(Guid id) =>
        Ok(await team.ResendInviteAsync(Access, UserId, id));

    [HttpDelete("invites/{id:guid}")]
    [RequirePermission(WorkspacePermissions.TeamManage)]
    [EndpointSummary("Withdraw an invite")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Revoke(Guid id)
    {
        await team.RevokeInviteAsync(Access, id);
        return NoContent();
    }

    [HttpPut("members/{userId}")]
    [RequirePermission(WorkspacePermissions.TeamManage)]
    [EndpointSummary("Change what a member may do")]
    [EndpointDescription("Replaces the member's grants. It takes effect on their next request; the copy in their token catches up when they next switch or sign in.")]
    [ProducesResponseType(typeof(TeamMemberResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TeamMemberResponse>> UpdateMember(string userId, [FromBody] UpdateMemberRequest request) =>
        Ok(await team.UpdateMemberAsync(Access, userId, request));

    [HttpDelete("members/{userId}")]
    [RequirePermission(WorkspacePermissions.TeamManage)]
    [EndpointSummary("Remove a member from the team")]
    [EndpointDescription("Takes away every grant. Their next request in any of the owner's workspaces is refused.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveMember(string userId)
    {
        await team.RemoveMemberAsync(Access, userId);
        return NoContent();
    }
}

/// <summary>An invite link, from the person it was sent to.</summary>
[ApiController]
[Route("api/invites")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Monitor(Area = "team")]
public sealed class InvitesController(ITeamService team, IAuthService auth, IInviteSignUpService signUp) : ControllerBase
{
    [HttpGet("{token}")]
    [AllowAnonymous]
    [EndpointSummary("What an invite link is for")]
    [EndpointDescription("Readable before signing in, so the page can say whose team it is and which address to sign in with.")]
    [ProducesResponseType(typeof(InvitePreviewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<InvitePreviewResponse>> Preview(string token) =>
        Ok(await team.PreviewInviteAsync(token));

    [HttpPost("{token}/sign-up")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Register)]
    [EndpointSummary("Create an account from an invite, and join")]
    [EndpointDescription("For an invited address with no account yet. Makes the account (already verified: the link was emailed to it), accepts the invite and returns a session in the team's workspace. The same login then works for every team and business.")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AuthResponse>> SignUp(string token, [FromBody] InviteSignUpRequest request) =>
        Ok(await signUp.SignUpAndAcceptAsync(token, request));

    [HttpPost("{token}/accept")]
    [Authorize]
    [AllowWithoutWorkspace]
    [EndpointSummary("Accept an invite")]
    [EndpointDescription("Joins the signed-in person to the team, if their address is the one invited, and returns a session working in the first workspace it reaches.")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AuthResponse>> Accept(string token)
    {
        string userId = User.UserId() ?? throw new UnauthorizedException("Sign in to continue.");
        string workspaceId = await team.AcceptInviteAsync(userId, token);
        return Ok(await auth.SwitchWorkspaceAsync(userId, workspaceId));
    }
}
