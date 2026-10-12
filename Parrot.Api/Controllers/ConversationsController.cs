using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Parrot.Application.Conversations;
using Parrot.Application.Integrations;
using Parrot.Domain.Exceptions;
using Parrot.Api.Monitoring;

namespace Parrot.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/conversations")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Monitor(Area = "conversations")]
public sealed class ConversationsController(IConversationStore store, ConversationService service,
    IWhatsAppConnectionRepository connections, IFacebookConnectionRepository facebook,
    IInstagramConnectionRepository instagram) : ControllerBase
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
        // A thread can be on a WhatsApp number, a Facebook Page or an Instagram account.
        if (await connections.GetByOwnerAndIdAsync(Owner, connectionId) is null
            && !await facebook.IsOwnedByAsync(connectionId, Owner, cancellationToken)
            && !await instagram.IsOwnedByAsync(connectionId, Owner, cancellationToken))
            throw new NotFoundException("Connection not found.");
        return Ok(await store.ReadAsync(Owner, connectionId, sender, before, cancellationToken));
    }

    [Monitor("conversations.send", SlowMs = 3000)] // waits on the WhatsApp Cloud API
    [HttpPost("{connectionId:guid}/messages")]
    public async Task<IActionResult> Send(Guid connectionId, [FromQuery] string sender,
        [FromBody] SendReplyRequest request, CancellationToken cancellationToken) =>
        Ok(await service.SendAsync(Owner, connectionId, sender, request, cancellationToken));
}
