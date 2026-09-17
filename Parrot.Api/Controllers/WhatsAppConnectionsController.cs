using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Integrations;

namespace Parrot.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/whatsapp/connections")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
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
        WhatsAppConnectionSetupResponse result = await _service.CreateAsync(GetUserId(), request);
        return CreatedAtAction(nameof(GetById), new { id = result.Connection.Id }, result);
    }

    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await _service.GetAllAsync(GetUserId()));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id) => Ok(await _service.GetByIdAsync(GetUserId(), id));

    [HttpGet("{id:guid}/setup")]
    public async Task<IActionResult> GetSetup(Guid id) => Ok(await _service.GetSetupAsync(GetUserId(), id));

    [HttpPost("{id:guid}/disable")]
    public async Task<IActionResult> Disable(Guid id)
    {
        await _service.DisableAsync(GetUserId(), id);
        return NoContent();
    }

    [HttpPut("{id:guid}/account")]
    public async Task<IActionResult> ConfigureAccount(Guid id, [FromBody] WhatsAppAccountRequest request)
    {
        await _service.ConfigureAccountAsync(GetUserId(), id, request);
        return NoContent();
    }

    private string GetUserId() =>
        User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? User.FindFirstValue("sub")
        ?? throw new InvalidOperationException("User ID claim not found.");
}
