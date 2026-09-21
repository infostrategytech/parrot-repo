using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Parrot.Application.Conversations;
using Parrot.Application.Integrations;
using Parrot.Domain.Exceptions;

namespace Parrot.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/conversations")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ConversationsController(IConversationStore store, ConversationService service,
    IWhatsAppConnectionRepository connections) : ControllerBase
{
    private string Owner => User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new UnauthorizedException("Sign in to continue.");

    [HttpGet]
    public async Task<IActionResult> List(int skip = 0, int take = 50, CancellationToken cancellationToken = default) =>
        Ok(await store.ListAsync(Owner, Math.Max(0, skip), Math.Clamp(take, 1, 100), cancellationToken));

    [HttpGet("{connectionId:guid}/messages")]
    public async Task<IActionResult> Messages(Guid connectionId, [FromQuery] string sender,
        [FromQuery] string? before, CancellationToken cancellationToken)
    {
        if (await connections.GetByOwnerAndIdAsync(Owner, connectionId) is null)
            throw new NotFoundException("Connection not found.");
        return Ok(await store.ReadAsync(Owner, connectionId, sender, before, cancellationToken));
    }

    [HttpPost("{connectionId:guid}/messages")]
    public async Task<IActionResult> Send(Guid connectionId, [FromQuery] string sender,
        [FromBody] SendReplyRequest request, CancellationToken cancellationToken) =>
        Ok(await service.SendAsync(Owner, connectionId, sender, request, cancellationToken));
}
