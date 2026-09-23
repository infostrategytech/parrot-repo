using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Integrations;

namespace Parrot.Api.Rest.Controllers;

[ApiController]
[Authorize]
[Route("api/facebook/setup")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class FacebookSetupController(IFacebookConnectionService connections) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        string? owner = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(owner)) return Unauthorized();
        return Ok(await connections.GetSetupAsync(owner, cancellationToken));
    }

    [HttpPut("page")]
    public async Task<IActionResult> SavePage([FromBody] FacebookPageRequest request, CancellationToken cancellationToken)
    {
        string? owner = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(owner)) return Unauthorized();
        await connections.SavePageAsync(owner, request, cancellationToken);
        return NoContent();
    }
}
