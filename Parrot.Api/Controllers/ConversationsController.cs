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
public sealed class ConversationsController(IConversationStore store, IConversationReadStore reads, ConversationService service,
    IWhatsAppConnectionRepository connections, IFacebookConnectionRepository facebook,
    ITikTokConnectionRepository tikTokConnections, IInstagramConnectionRepository instagram) : ControllerBase
{
    private string Owner => User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new UnauthorizedException("Sign in to continue.");

    [HttpGet]
    public async Task<IActionResult> List(int skip = 0, int take = 50, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ConversationSummary> page = await store.ListAsync(Owner, Math.Max(0, skip), Math.Clamp(take, 1, 100), cancellationToken);
        IReadOnlyDictionary<(Guid, string), int> unread = await reads.CountUnreadAsync(Owner, Owner,
            page.Select(c => (c.ConnectionId, c.Sender)).ToList(), cancellationToken);
        return Ok(page.Select(c => c with { UnreadCount = unread.GetValueOrDefault((c.ConnectionId, c.Sender)) }));
    }

    // Across every conversation and channel, not only the loaded page: it drives the inbox badge.
    [HttpGet("unread")]
    public async Task<IActionResult> Unread(CancellationToken cancellationToken) =>
        Ok(new UnreadSummary(await reads.TotalUnreadAsync(Owner, Owner, cancellationToken)));

    // Marks the thread read up to the newest message the agent has seen. A POST, not a side effect of
    // reading it: the inbox re-reads the open thread on a timer, which must not read what nobody saw.
    [HttpPost("{connectionId:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid connectionId, [FromQuery] string sender,
        [FromBody] MarkReadRequest request, CancellationToken cancellationToken)
    {
        await EnsureConnectionOwnedAsync(connectionId, cancellationToken);
        await reads.MarkReadAsync(Owner, Owner, connectionId, sender, request.UpToMessageId, cancellationToken);
        IReadOnlyDictionary<(Guid, string), int> unread = await reads.CountUnreadAsync(Owner, Owner, [(connectionId, sender)], cancellationToken);
        return Ok(new MarkReadResult(unread.GetValueOrDefault((connectionId, sender)), await reads.TotalUnreadAsync(Owner, Owner, cancellationToken)));
    }

    [HttpGet("{connectionId:guid}/messages")]
    public async Task<IActionResult> Messages(Guid connectionId, [FromQuery] string sender,
        [FromQuery] string? before, CancellationToken cancellationToken)
    {
        await EnsureConnectionOwnedAsync(connectionId, cancellationToken);
        return Ok(await store.ReadAsync(Owner, connectionId, sender, before, cancellationToken));
    }

    [Monitor("conversations.send", SlowMs = 3000)] // waits on the WhatsApp Cloud API or TikTok
    [HttpPost("{connectionId:guid}/messages")]
    public async Task<IActionResult> Send(Guid connectionId, [FromQuery] string sender,
        [FromBody] SendReplyRequest request, CancellationToken cancellationToken) =>
        Ok(await service.SendAsync(Owner, connectionId, sender, request, cancellationToken));

    // A thread can be on a WhatsApp number, a Facebook Page, the TikTok account or an Instagram account.
    private async Task EnsureConnectionOwnedAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        if (await connections.GetByOwnerAndIdAsync(Owner, connectionId) is null
            && !await facebook.IsOwnedByAsync(connectionId, Owner, cancellationToken)
            && (await tikTokConnections.GetByOwnerAsync(Owner, cancellationToken))?.Id != connectionId
            && !await instagram.IsOwnedByAsync(connectionId, Owner, cancellationToken))
            throw new NotFoundException("Connection not found.");
    }
}
