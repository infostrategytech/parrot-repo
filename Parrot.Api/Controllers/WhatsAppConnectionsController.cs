using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Parrot.Api.Monitoring;
using Parrot.Api.Security;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Integrations;
using Parrot.Application.Teams;

namespace Parrot.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/whatsapp/connections")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Monitor(Area = "whatsapp.connections")]
[RequireChannel(WorkspaceChannels.WhatsApp)]
public class WhatsAppConnectionsController : ControllerBase
{
    private readonly IWhatsAppConnectionService _service;
    private readonly IWhatsAppCatalogueSyncService _catalogueSyncService;

    public WhatsAppConnectionsController(
        IWhatsAppConnectionService service,
        IWhatsAppCatalogueSyncService catalogueSyncService)
    {
        _service = service;
        _catalogueSyncService = catalogueSyncService;
    }

    [HttpPost]
    [RequirePermission(WorkspacePermissions.ChannelsConnect)]
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
    [RequirePermission(WorkspacePermissions.ChannelsDisconnect)]
    public async Task<IActionResult> Disable(Guid id)
    {
        await _service.DisableAsync(Owner(), id);
        return NoContent();
    }

    [HttpPut("{id:guid}/account")]
    [RequirePermission(WorkspacePermissions.ChannelsConnect)]
    public async Task<IActionResult> ConfigureAccount(Guid id, [FromBody] WhatsAppAccountRequest request)
    {
        await _service.ConfigureAccountAsync(Owner(), id, request);
        return NoContent();
    }

    [HttpPut("{id:guid}/access-token")]
    [RequirePermission(WorkspacePermissions.ChannelsConnect)]
    public async Task<IActionResult> SaveAccessToken(Guid id, [FromBody] WhatsAppAccessTokenRequest request)
    {
        await _service.SaveAccessTokenAsync(Owner(), id, request);
        return NoContent();
    }

    [HttpPut("{id:guid}/app-secret")]
    [RequirePermission(WorkspacePermissions.ChannelsConnect)]
    public async Task<IActionResult> SaveAppSecret(Guid id, [FromBody] AppSecretRequest request)
    {
        await _service.SaveAppSecretAsync(Owner(), id, request);
        return NoContent();
    }

    [HttpPost("{id:guid}/catalogue/sync")]
    [RequirePermission(WorkspacePermissions.ChannelsConnect)]
    public async Task<IActionResult> SyncCatalogue(Guid id) =>
        Ok(await _catalogueSyncService.SyncAsync(Owner(), id, HttpContext.RequestAborted));

    [HttpGet("{id:guid}/catalogue/products")]
    public async Task<IActionResult> GetCatalogueProducts(
        Guid id, [FromQuery] int page = 1, [FromQuery] int pageSize = 50) =>
        Ok(await _catalogueSyncService.GetProductsAsync(Owner(), id, page, pageSize, HttpContext.RequestAborted));

    private string Owner() =>
        User.WorkspaceId()
        ?? throw new InvalidOperationException("User ID claim not found.");
}
