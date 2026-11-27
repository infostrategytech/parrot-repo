using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Parrot.Api.Security;
using Parrot.Application.Agents;
using Parrot.Application.Contracts.Agents;
using Parrot.Application.Teams;
using Parrot.Domain.Exceptions;

namespace Parrot.Api.Controllers;

/// <summary>
/// Temporary: the switch for agents sharing a number, for the workspaces in the live test (see MultiAgentSwitch).
/// It is always the token's workspace; there is no id to give.
/// </summary>
[ApiController]
[Route("api/multi-agent-switch")]
[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class MultiAgentSwitchController(MultiAgentSwitch multiAgent) : ControllerBase
{
    private string Owner => User.WorkspaceId()
        ?? throw new UnauthorizedException("Sign in to continue.");

    private string Reader => User.UserId()
        ?? throw new UnauthorizedException("Sign in to continue.");

    /// <summary>Whether the workspace has the switch and whether it is on: for the agents' screens and the Overview.</summary>
    [HttpGet]
    [RequireAnyPermission(WorkspacePermissions.AgentsCreate, WorkspacePermissions.AgentsEdit,
        WorkspacePermissions.PlaybooksCreate, WorkspacePermissions.PlaybooksEdit,
        WorkspacePermissions.PlaybooksDelete, WorkspacePermissions.PlaybooksPublish,
        WorkspacePermissions.ConversationsView, WorkspacePermissions.ConversationsTakeOver,
        WorkspacePermissions.AnalyticsDashboard)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken) =>
        Ok(await multiAgent.GetAsync(Owner, cancellationToken));

    [HttpPut]
    [RequirePermission(WorkspacePermissions.AgentsEdit)]
    public async Task<IActionResult> Set([FromBody] SetMultiAgentSwitchRequest request, CancellationToken cancellationToken) =>
        Ok(await multiAgent.SetAsync(Owner, Reader, request, cancellationToken));
}
