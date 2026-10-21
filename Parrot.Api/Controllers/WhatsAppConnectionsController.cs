using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Integrations;
using Parrot.Api.Monitoring;
using Parrot.Api.Security;

namespace Parrot.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/whatsapp/connections")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Monitor(Area = "whatsapp.connections")]
public class WhatsAppConnectionsController : ControllerBase
{
    private readonly IWhatsAppConnectionService _service;

    public WhatsAppConnectionsController(IWhatsAppConnectionService service)
    {
        _service = service;
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateWhatsAppConnectionRequest request)
    {
        WhatsAppConnectionSetupResponse result = await _service.CreateAsync(Owner(), request);
        return CreatedAtAction(nameof(GetById), new { id = result.Connection.Id }, result);
    }

    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await _service.GetAllAsync(Owner()));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id) => Ok(await _service.GetByIdAsync(Owner(), id));

    [HttpGet("{id:guid}/setup")]
    public async Task<IActionResult> GetSetup(Guid id) => Ok(await _service.GetSetupAsync(Owner(), id));

    [HttpPost("{id:guid}/disable")]
    public async Task<IActionResult> Disable(Guid id)
    {
        await _service.DisableAsync(Owner(), id);
        return NoContent();
    }

    [HttpPut("{id:guid}/account")]
    public async Task<IActionResult> ConfigureAccount(Guid id, [FromBody] WhatsAppAccountRequest request)
    {
        await _service.ConfigureAccountAsync(Owner(), id, request);
        return NoContent();
    }

    [HttpPut("{id:guid}/access-token")]
    public async Task<IActionResult> SaveAccessToken(Guid id, [FromBody] WhatsAppAccessTokenRequest request)
    {
        await _service.SaveAccessTokenAsync(Owner(), id, request);
        return NoContent();
    }

    [HttpPut("{id:guid}/app-secret")]
    public async Task<IActionResult> SaveAppSecret(Guid id, [FromBody] AppSecretRequest request)
    {
        await _service.SaveAppSecretAsync(Owner(), id, request);
        return NoContent();
    }

    private string Owner() =>
        User.WorkspaceId()
        ?? throw new InvalidOperationException("User ID claim not found.");
}
