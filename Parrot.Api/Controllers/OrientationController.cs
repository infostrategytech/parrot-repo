using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Parrot.Api.Monitoring;
using Parrot.Api.Security;
using Parrot.Application.Agents;
using Parrot.Application.Teams;
using Parrot.Domain.Exceptions;

namespace Parrot.Api.Controllers;

/// <summary>
/// "Tell me about your business", by voice: a signed ElevenLabs link for the signed-in owner,
/// which the browser connects to directly. The AI behind it learns who it is talking to from
/// the service token Parrot sends for the owner, so the browser never names a tenant.
/// </summary>
[ApiController]
[Authorize]
[Route("api/orientation")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Monitor(Area = "orientation")]
public sealed class OrientationController(IOrientationClient orientation) : ControllerBase
{
    private string Owner => User.WorkspaceId()
        ?? throw new UnauthorizedException("Sign in to continue.");

    [HttpPost("voice/session")]
    [RequirePermission(WorkspacePermissions.WorkspaceManage)]
    public async Task<ActionResult<OrientationVoiceSession>> StartVoice(CancellationToken cancellationToken) =>
        Ok(await orientation.StartVoiceSessionAsync(Owner, cancellationToken));
}
