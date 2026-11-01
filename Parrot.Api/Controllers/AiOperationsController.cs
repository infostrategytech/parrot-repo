using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Parrot.Api.Monitoring;
using Parrot.Api.Security;
using Parrot.Application.Agents;
using Parrot.Application.Teams;
using Parrot.Domain.Exceptions;

namespace Parrot.Api.Controllers;

/// <summary>
/// The AI Operations screens' figures and the owner's decisions. Everything
/// is scoped to the signed-in owner by the token; an approval id in the path
/// is only acted on when it was asked of that owner.
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

    /// <summary>The approvals and escalations waiting for the owner.</summary>
    [HttpGet("waiting")]
    [RequirePermission(WorkspacePermissions.ConversationsTakeOver)]
    public async Task<IActionResult> Waiting(CancellationToken cancellationToken) =>
        Ok(await operations.WaitingAsync(Owner, cancellationToken));

    /// <summary>Decides an approval: the orchestrator resumes the turn, and the decision is kept here.</summary>
    [HttpPost("approvals/{id}")]
    [RequirePermission(WorkspacePermissions.ConversationsTakeOver)]
    public async Task<IActionResult> Decide(string id, [FromBody] ApprovalDecisionRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ValidationException("Supply the approval to decide.");

        return Ok(await operations.DecideApprovalAsync(Owner, id.Trim(), request, cancellationToken));
    }
}
