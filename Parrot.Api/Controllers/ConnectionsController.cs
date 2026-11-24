using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Parrot.Api.Monitoring;
using Parrot.Api.Security;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Teams;
using Parrot.Domain.Enums;

namespace Parrot.Api.Controllers;

/// <summary>The connections in the session's workspace, across every channel.</summary>
[ApiController]
[Authorize]
[Route("api/connections")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Monitor(Area = "connections")]
public sealed class ConnectionsController(IWorkspaceConnectionDirectory directory) : ControllerBase
{
    [HttpGet]
    [EndpointSummary("The connection ids in this workspace")]
    [EndpointDescription("Every connection in the session's workspace, disabled ones included, with its channel key. A member limited to some channels sees only the connections on those channels.")]
    [ProducesResponseType(typeof(IReadOnlyList<WorkspaceConnectionResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<WorkspaceConnectionResponse>>> ListAsync()
    {
        WorkspaceAccess access = HttpContext.WorkspaceAccess();
        IReadOnlyList<(Guid Id, MessagingPlatform Platform)> connections = await directory.ListAsync(access.Workspace.Id);
        return Ok(connections
            .Where(c => access.CanReach(c.Id))
            .Select(c => new WorkspaceConnectionResponse(c.Id, WorkspaceChannels.KeyOf(c.Platform)))
            .ToList());
    }
}
