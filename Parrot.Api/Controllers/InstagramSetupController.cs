using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Parrot.Api.Monitoring;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Integrations;
using Parrot.Api.Security;

namespace Parrot.Api.Rest.Controllers;

[ApiController]
[Authorize]
[Route("api/instagram/setup")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
// Manual Instagram setup for the signed-in business; the public webhook lives in InstagramWebhookController.
[Monitor(Area = "instagram.setup")]
public sealed class InstagramSetupController(IInstagramConnectionService connections) : ControllerBase
{
    private string? Owner => User.WorkspaceId();

    // GET /api/instagram/setup - the callback URL and verify token, creating the connection if needed.
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken) =>
        Owner is { Length: > 0 } owner ? Ok(await connections.GetSetupAsync(owner, cancellationToken)) : Unauthorized();

    // PUT /api/instagram/setup/account - the access token; Parrot reads the account and subscribes it.
    [Monitor("instagram.setup.account", SlowMs = 3000)] // waits on graph.instagram.com
    [HttpPut("account")]
    public async Task<IActionResult> SaveAccount([FromBody] InstagramAccountRequest request, CancellationToken cancellationToken)
    {
        if (Owner is not { Length: > 0 } owner) return Unauthorized();
        await connections.SaveAccountAsync(owner, request, cancellationToken);
        return NoContent();
    }

    // PUT /api/instagram/setup/app-secret - the Instagram app secret that signs this account's webhooks.
    [HttpPut("app-secret")]
    public async Task<IActionResult> SaveAppSecret([FromBody] AppSecretRequest request, CancellationToken cancellationToken)
    {
        if (Owner is not { Length: > 0 } owner) return Unauthorized();
        await connections.SaveAppSecretAsync(owner, request, cancellationToken);
        return NoContent();
    }

    // POST /api/instagram/setup/disable - its webhooks are refused from now on.
    [HttpPost("disable")]
    public async Task<IActionResult> Disable(CancellationToken cancellationToken)
    {
        if (Owner is not { Length: > 0 } owner) return Unauthorized();
        await connections.DisableAsync(owner, cancellationToken);
        return NoContent();
    }
}
