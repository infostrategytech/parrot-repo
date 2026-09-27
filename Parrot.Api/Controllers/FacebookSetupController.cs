using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Integrations;
using Parrot.Api.Monitoring;

namespace Parrot.Api.Rest.Controllers;

[ApiController]
[Authorize]
[Route("api/facebook/setup")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
// Authenticated setup endpoints for Messenger; the public webhook lives in FacebookWebhookController.
[Monitor(Area = "facebook.setup")]
public sealed class FacebookSetupController(IFacebookConnectionService connections) : ControllerBase
{
    // GET /api/facebook/setup - returns webhook URL and verify token, creating the connection if needed.
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        string? owner = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(owner)) return Unauthorized();
        return Ok(await connections.GetSetupAsync(owner, cancellationToken));
    }

    // PUT /api/facebook/setup/page - saves the Page ID, name and access token after verification.
    [HttpPut("page")]
    public async Task<IActionResult> SavePage([FromBody] FacebookPageRequest request, CancellationToken cancellationToken)
    {
        string? owner = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(owner)) return Unauthorized();
        await connections.SavePageAsync(owner, request, cancellationToken);
        return NoContent();
    }
}
