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
public sealed class ConversationsController(IConversationStore store, IConversationReadStore reads, IConversationFlagStore flags, ChannelReadReceipts channelReads, ConversationService service,
    IWhatsAppConnectionRepository connections, IFacebookConnectionRepository facebook,
    ITikTokConnectionRepository tikTokConnections, IInstagramConnectionRepository instagram) : ControllerBase
{
    private string Owner => User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new UnauthorizedException("Sign in to continue.");

    [HttpGet]
    public async Task<IActionResult> List(int skip = 0, int take = 50, CancellationToken cancellationToken = default)
    {
        take = Math.Clamp(take, 1, 100);
        skip = Math.Max(0, skip);
        IReadOnlyList<ConversationSummary> page = await store.ListAsync(Owner, skip, take, cancellationToken);
        IReadOnlyDictionary<(Guid Connection, string Sender), ConversationFlags> marks = await flags.ListAsync(Owner, cancellationToken);

        // Pinned conversations lead the first page wherever they fall in the newest-first order, so a pin older
        // than the page is still there. A page that holds one already is not repeated by it.
        List<ConversationSummary> rows = [.. page];
        if (skip == 0)
        {
            HashSet<(Guid, string)> listed = [.. page.Select(c => (c.ConnectionId, c.Sender))];
            List<(Guid, string)> missing = [.. marks.Where(m => m.Value.Pinned).Select(m => m.Key).Where(k => !listed.Contains(k))];
            rows.AddRange(await store.FindAsync(Owner, missing, cancellationToken));
        }

        IReadOnlyDictionary<(Guid, string), int> unread = await reads.CountUnreadAsync(Owner, Owner,
            rows.Select(c => (c.ConnectionId, c.Sender)).ToList(), cancellationToken);
        return Ok(rows
            .Select(c =>
            {
                marks.TryGetValue((c.ConnectionId, c.Sender), out ConversationFlags? mark);
                return c with
                {
                    UnreadCount = unread.GetValueOrDefault((c.ConnectionId, c.Sender)),
                    Pinned = mark?.Pinned ?? false,
                    MarkedUnread = mark?.MarkedUnread ?? false,
                };
            })
            .OrderByDescending(c => c.Pinned).ThenByDescending(c => c.UpdatedAt));
    }

    // Across every conversation and channel, not only the loaded page: it drives the inbox badge.
    [HttpGet("unread")]
    public async Task<IActionResult> Unread(CancellationToken cancellationToken) =>
        Ok(new UnreadSummary(await TotalUnreadAsync(cancellationToken)));

    // Marks the thread read up to the newest message the agent has seen. A POST, not a side effect of
    // reading it: the inbox re-reads the open thread on a timer, which must not read what nobody saw.
    // It also lifts a "mark as unread": the agent has now looked at the conversation.
    [HttpPost("{connectionId:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid connectionId, [FromQuery] string sender,
        [FromBody] MarkReadRequest request, CancellationToken cancellationToken)
    {
        await EnsureConnectionOwnedAsync(connectionId, cancellationToken);
        ReadAdvance? read = await reads.MarkReadAsync(Owner, Owner, connectionId, sender, request.UpToMessageId, cancellationToken);
        await flags.SetMarkedUnreadAsync(Owner, connectionId, sender, false, cancellationToken);

        // Only when this moved the position: a repeat must not tell the channel again.
        if (read is not null) await channelReads.SendAsync(Owner, read, cancellationToken);
        IReadOnlyDictionary<(Guid, string), int> unread = await reads.CountUnreadAsync(Owner, Owner, [(connectionId, sender)], cancellationToken);
        return Ok(new MarkReadResult(unread.GetValueOrDefault((connectionId, sender)), await TotalUnreadAsync(cancellationToken)));
    }

    // Brings the unread indicator back for a conversation the agent has read, to return to it later.
    [HttpPut("{connectionId:guid}/marked-unread")]
    public async Task<IActionResult> SetMarkedUnread(Guid connectionId, [FromQuery] string sender,
        [FromBody] SetMarkedUnreadRequest request, CancellationToken cancellationToken)
    {
        await EnsureConnectionOwnedAsync(connectionId, cancellationToken);
        await flags.SetMarkedUnreadAsync(Owner, connectionId, sender, request.MarkedUnread, cancellationToken);
        return Ok(new UnreadSummary(await TotalUnreadAsync(cancellationToken)));
    }

    [HttpPut("{connectionId:guid}/pin")]
    public async Task<IActionResult> SetPinned(Guid connectionId, [FromQuery] string sender,
        [FromBody] SetPinnedRequest request, CancellationToken cancellationToken)
    {
        await EnsureConnectionOwnedAsync(connectionId, cancellationToken);
        await flags.SetPinnedAsync(Owner, connectionId, sender, request.Pinned, cancellationToken);
        return Ok(new SetPinnedRequest(request.Pinned));
    }

    // Unread customer messages, plus one for each conversation marked unread that has none of its own:
    // the inbox badge counts what the agent has set aside to come back to.
    private async Task<int> TotalUnreadAsync(CancellationToken ct)
    {
        int total = await reads.TotalUnreadAsync(Owner, Owner, ct);
        List<(Guid Connection, string Sender)> marked = [.. (await flags.ListAsync(Owner, ct)).Where(f => f.Value.MarkedUnread).Select(f => f.Key)];
        if (marked.Count == 0) return total;
        IReadOnlyDictionary<(Guid, string), int> counts = await reads.CountUnreadAsync(Owner, Owner, marked, ct);
        return total + marked.Count(m => counts.GetValueOrDefault(m) == 0);
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
