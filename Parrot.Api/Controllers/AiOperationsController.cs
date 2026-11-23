using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Parrot.Api.Monitoring;
using Parrot.Api.Security;
using Parrot.Application.Agents;
using Parrot.Application.Teams;
using Parrot.Domain.Exceptions;

namespace Parrot.Api.Controllers;

/// <summary>
/// The AI Operations screens' figures and the team's decisions. Everything
/// is scoped to the token's workspace; an approval id in the path is only
/// acted on while it waits on that workspace's list, on a channel the caller can open.
/// </summary>
[ApiController]
[Route("api/ai-operations")]
[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Monitor(Area = "ai-operations")]
public sealed class AiOperationsController(IAiOperationsService operations) : ControllerBase
{
    private string Owner => User.WorkspaceId()
        ?? throw new UnauthorizedException("Sign in to continue.");

    private string Reader => User.UserId()
        ?? throw new UnauthorizedException("Sign in to continue.");

    /// <summary>Today's figures, for every agent and channel or for one of each.</summary>
    [HttpGet("today")]
    [RequirePermission(WorkspacePermissions.AnalyticsDashboard)]
    public async Task<IActionResult> Today([FromQuery] string? agentId, [FromQuery] string? channel, CancellationToken cancellationToken) =>
        Ok(await operations.TodayAsync(Owner, agentId, channel, cancellationToken));

    /// <summary>Conversations per day for the last seven days, oldest first.</summary>
    [HttpGet("week")]
    [RequirePermission(WorkspacePermissions.AnalyticsDashboard)]
    public async Task<IActionResult> Week([FromQuery] string? agentId, [FromQuery] string? channel, CancellationToken cancellationToken) =>
        Ok(await operations.WeekAsync(Owner, agentId, channel, cancellationToken));

    /// <summary>The latest conversations, newest first.</summary>
    [HttpGet("recent")]
    [RequirePermission(WorkspacePermissions.AnalyticsDashboard)]
    public async Task<IActionResult> Recent(
        [FromQuery] string? agentId,
        [FromQuery] string? channel,
        [FromQuery] int limit = AiOperationsService.DefaultRecent,
        CancellationToken cancellationToken = default) =>
        Ok(await operations.RecentAsync(Owner, agentId, channel, limit, cancellationToken));

    /// <summary>What the agents handed to the team and is still waiting (the AI service's list), on the channels the caller can open.</summary>
    [HttpGet("waiting")]
    [RequirePermission(WorkspacePermissions.ConversationsTakeOver)]
    public async Task<IActionResult> Waiting(CancellationToken cancellationToken) =>
        Ok(await operations.WaitingAsync(Owner, HttpContext.SessionToken(), HttpContext.WorkspaceAccess().AllowedConnections, cancellationToken));

    /// <summary>Decides an approval as the signed-in person, on the channels they can open: the agent resumes its turn.</summary>
    [HttpPost("approvals/{id}")]
    [RequirePermission(WorkspacePermissions.ConversationsTakeOver)]
    public async Task<IActionResult> Decide(string id, [FromBody] ApprovalDecisionRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ValidationException("Supply the approval to decide.");

        return Ok(await operations.DecideApprovalAsync(
            Owner, HttpContext.SessionToken(), HttpContext.WorkspaceAccess().AllowedConnections, Reader, id.Trim(), request, cancellationToken));
    }
}
