using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Parrot.Api.Media;
using Parrot.Api.Monitoring;
using Parrot.Api.Security;
using Parrot.Api.Utils;
using Parrot.Application.Agents;
using Parrot.Application.Conversations;
using Parrot.Application.Integrations;
using Parrot.Application.Logging;
using Parrot.Application.Teams;
using Parrot.Domain.Exceptions;

namespace Parrot.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/conversations")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Monitor(Area = "conversations")]
[RequirePermission(WorkspacePermissions.ConversationsView)]
public sealed class ConversationsController(IConversationStore store, IConversationReadStore reads, IConversationFlagStore flags, IConversationNoteStore notes, ChannelReadReceipts channelReads, MediaLinks mediaLinks, ConversationService service,
    IWhatsAppConnectionRepository connections, IFacebookConnectionRepository facebook,
    ITikTokConnectionRepository tikTokConnections, IInstagramConnectionRepository instagram,
    IHumanTakeoverStore humanTakeover, IAgentOrchestratorClient orchestrator, IMemoryCache cache, IOptions<OmniAiSettings> omniAi,
    IAiOperationsService operations, IAppLogger<ConversationsController> logger) : ControllerBase
{
    // The AI service's agents for the workspace, shared for 30 s: they rarely change, and every inbox read needs them.
    private async Task<T> CachedAsync<T>(string key, Func<Task<T>> read) where T : class
    {
        if (cache.TryGetValue(key, out T? cached) && cached is not null) return cached;
        T fresh = await read();
        cache.Set(key, fresh, TimeSpan.FromSeconds(30));
        return fresh;
    }

    // The workspace owns the conversations; what has been read, what is pinned
    // or set aside, and who wrote a note, belong to the person signed in.
    private string Owner => User.WorkspaceId()
        ?? throw new UnauthorizedException("Sign in to continue.");

    private string Reader => User.UserId()
        ?? throw new UnauthorizedException("Sign in to continue.");

    // The connections a channel-limited team member reaches; null for everyone else.
    private IReadOnlySet<Guid>? Reach => HttpContext.WorkspaceAccess().AllowedConnections;

    [HttpGet]
    public async Task<IActionResult> List(int skip = 0, int take = 50, CancellationToken cancellationToken = default)
    {
        take = Math.Clamp(take, 1, 100);
        skip = Math.Max(0, skip);
        IReadOnlyList<ConversationSummary> page = Reach is { } reach
            ? await store.ListAsync(Owner, reach, skip, take, cancellationToken)
            : await store.ListAsync(Owner, skip, take, cancellationToken);
        IReadOnlyDictionary<(Guid Connection, string Sender), ConversationFlags> marks = await ReachableFlagsAsync(cancellationToken);

        // Open agent questions and approvals, for the people the waiting list is for.
        Dictionary<(Guid, string), ConversationQuestions> questions = HttpContext.WorkspaceAccess().Can(WorkspacePermissions.ConversationsTakeOver)
            ? (await operations.OpenByConversationAsync(Owner, HttpContext.SessionToken(), Reach, cancellationToken)).ToDictionary(q => (q.ConnectionId, q.Sender))
            : [];

        // Pinned conversations, and those with an open question, lead the first page wherever they fall in the
        // newest-first order, so an older one is still there. A page that holds one already is not repeated by it.
        List<ConversationSummary> rows = [.. page];
        if (skip == 0)
        {
            HashSet<(Guid, string)> listed = [.. page.Select(c => (c.ConnectionId, c.Sender))];
            List<(Guid, string)> missing = [.. marks.Where(m => m.Value.Pinned).Select(m => m.Key).Concat(questions.Keys)
                .Where(k => !listed.Contains(k)).Distinct()];
            rows.AddRange(await store.FindAsync(Owner, missing, cancellationToken));
        }

        IReadOnlyDictionary<(Guid, string), int> unread = await reads.CountUnreadAsync(Owner, Reader,
            rows.Select(c => (c.ConnectionId, c.Sender)).ToList(), cancellationToken);

        // One call to the AI service returns every live agent: by connection when several agents can share one
        // (OmniAi:MultiAgentEnabled), otherwise by channel platform, as before.
        // The result is cached per workspace for 30 s; agents rarely change and the cache stays fresh enough.
        // If the AI service is unreachable the inbox degrades gracefully: no channel agent shown.
        bool sharesChannels = omniAi.Value.MultiAgentEnabled;
        IReadOnlyDictionary<string, ConversationAgentRef> agentByChannel = new Dictionary<string, ConversationAgentRef>();
        IReadOnlyDictionary<Guid, IReadOnlyList<ConversationAgentRef>> agentsByConnection = new Dictionary<Guid, IReadOnlyList<ConversationAgentRef>>();
        try
        {
            if (sharesChannels)
                agentsByConnection = await CachedAsync($"connection_agents:{Owner}", () => orchestrator.GetConnectionAgentsAsync(Owner, cancellationToken));
            else
                agentByChannel = await CachedAsync($"channel_agents:{Owner}", () => orchestrator.GetChannelAgentMapAsync(Owner, cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "channel_agents.unavailable OwnerId={OwnerId} — inbox degrades: no channel agent shown", Owner);
        }

        // The orchestrator names its live agents; Parrot's own agent list may not know the one that asked.
        Dictionary<string, ConversationAgentRef> liveAgents = agentByChannel.Values
            .Concat(agentsByConnection.Values.SelectMany(agents => agents))
            .DistinctBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);

        // One batch query for active human takeovers across the whole page.
        List<(Guid, string)> keys = [.. rows.Select(c => (c.ConnectionId, c.Sender))];
        IReadOnlyDictionary<(Guid, string), DateTime?> activeTakeovers =
            await humanTakeover.GetActiveAsync(Owner, keys, cancellationToken);

        return Ok(rows
            .Select(c =>
            {
                marks.TryGetValue((c.ConnectionId, c.Sender), out ConversationFlags? mark);
                questions.TryGetValue((c.ConnectionId, c.Sender), out ConversationQuestions? open);
                bool hasActiveTakeover = activeTakeovers.ContainsKey((c.ConnectionId, c.Sender));
                IReadOnlyList<ConversationAgentRef> onNumber = sharesChannels
                    ? agentsByConnection.GetValueOrDefault(c.ConnectionId) ?? []
                    : agentByChannel.GetValueOrDefault(c.Channel) is { } only ? [only] : [];
                // The first of them, for a web app from before agents could share a channel.
                ConversationAgentRef? channelAgent = onNumber.FirstOrDefault();
                return c with
                {
                    UnreadCount = unread.GetValueOrDefault((c.ConnectionId, c.Sender)),
                    Pinned = mark?.Pinned ?? false,
                    MarkedUnread = mark?.MarkedUnread ?? false,
                    ChannelAgent = channelAgent,
                    Agents = onNumber,
                    ControlUntil = activeTakeovers.TryGetValue((c.ConnectionId, c.Sender), out DateTime? until) ? until : null,
                    AiState = hasActiveTakeover ? "handoff" : channelAgent is not null ? "active" : null,
                    NeedsAnswer = open?.NeedsAnswer ?? false,
                    PendingApproval = open?.Approval,
                    AiAgent = open?.Agent is { } asker ? liveAgents.GetValueOrDefault(asker.Id) ?? asker : null,
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
        ReadAdvance? read = await reads.MarkReadAsync(Owner, Reader, connectionId, sender, request.UpToMessageId, cancellationToken);
        await flags.SetMarkedUnreadAsync(Reader, connectionId, sender, false, cancellationToken);

        // Only when this moved the position: a repeat must not tell the channel again.
        if (read is not null) await channelReads.SendAsync(Owner, read, cancellationToken);
        IReadOnlyDictionary<(Guid, string), int> unread = await reads.CountUnreadAsync(Owner, Reader, [(connectionId, sender)], cancellationToken);
        return Ok(new MarkReadResult(unread.GetValueOrDefault((connectionId, sender)), await TotalUnreadAsync(cancellationToken)));
    }

    // Brings the unread indicator back for a conversation the agent has read, to return to it later.
    [HttpPut("{connectionId:guid}/marked-unread")]
    public async Task<IActionResult> SetMarkedUnread(Guid connectionId, [FromQuery] string sender,
        [FromBody] SetMarkedUnreadRequest request, CancellationToken cancellationToken)
    {
        await EnsureConnectionOwnedAsync(connectionId, cancellationToken);
        await flags.SetMarkedUnreadAsync(Reader, connectionId, sender, request.MarkedUnread, cancellationToken);
        return Ok(new UnreadSummary(await TotalUnreadAsync(cancellationToken)));
    }

    [HttpPut("{connectionId:guid}/pin")]
    public async Task<IActionResult> SetPinned(Guid connectionId, [FromQuery] string sender,
        [FromBody] SetPinnedRequest request, CancellationToken cancellationToken)
    {
        await EnsureConnectionOwnedAsync(connectionId, cancellationToken);
        await flags.SetPinnedAsync(Reader, connectionId, sender, request.Pinned, cancellationToken);
        return Ok(new SetPinnedRequest(request.Pinned));
    }

    // Internal notes: for the team only. They are not messages, so nothing here reaches the customer's channel.
    [HttpGet("{connectionId:guid}/notes")]
    public async Task<IActionResult> Notes(Guid connectionId, [FromQuery] string sender, CancellationToken cancellationToken)
    {
        await EnsureConnectionOwnedAsync(connectionId, cancellationToken);
        return Ok(await notes.ListAsync(Owner, connectionId, sender, cancellationToken));
    }

    [HttpPost("{connectionId:guid}/notes")]
    [RequirePermission(WorkspacePermissions.ConversationsReply)]
    public async Task<IActionResult> AddNote(Guid connectionId, [FromQuery] string sender,
        [FromBody] AddNoteRequest request, CancellationToken cancellationToken)
    {
        await EnsureConnectionOwnedAsync(connectionId, cancellationToken);
        string text = request.Text?.Trim() ?? "";
        if (text.Length == 0) throw new ValidationException("Write something to save as a note.");
        if (text.Length > IConversationNoteStore.MaxLength)
            throw new ValidationException($"A note can be at most {IConversationNoteStore.MaxLength} characters.");
        string author = User.FindFirstValue(ClaimTypes.Email) ?? User.FindFirstValue("email") ?? "Team member";
        return Ok(await notes.AddAsync(Owner, connectionId, sender, text, Reader, author, cancellationToken));
    }

    [HttpDelete("{connectionId:guid}/notes/{noteId}")]
    [RequirePermission(WorkspacePermissions.ConversationsReply)]
    public async Task<IActionResult> DeleteNote(Guid connectionId, string noteId, [FromQuery] string sender, CancellationToken cancellationToken)
    {
        await EnsureConnectionOwnedAsync(connectionId, cancellationToken);
        if (!await notes.DeleteAsync(Owner, connectionId, sender, noteId, cancellationToken)) throw new NotFoundException("Note not found.");
        return NoContent();
    }

    // Unread customer messages, plus one for each conversation marked unread that has none of its own:
    // the inbox badge counts what the agent has set aside to come back to.
    private async Task<int> TotalUnreadAsync(CancellationToken ct)
    {
        int total = await reads.TotalUnreadAsync(Owner, Reader, Reach, ct);
        List<(Guid Connection, string Sender)> marked = [.. (await ReachableFlagsAsync(ct)).Where(f => f.Value.MarkedUnread).Select(f => f.Key)];
        if (marked.Count == 0) return total;
        IReadOnlyDictionary<(Guid, string), int> counts = await reads.CountUnreadAsync(Owner, Reader, marked, ct);
        return total + marked.Count(m => counts.GetValueOrDefault(m) == 0);
    }

    [HttpGet("{connectionId:guid}/messages")]
    public async Task<IActionResult> Messages(Guid connectionId, [FromQuery] string sender,
        [FromQuery] string? before, CancellationToken cancellationToken)
    {
        await EnsureConnectionOwnedAsync(connectionId, cancellationToken);
        MessagePage page = await store.ReadAsync(Owner, connectionId, sender, before, cancellationToken);

        // The newest page also carries the agents' open questions here, for the people who can answer them.
        if (before is null && HttpContext.WorkspaceAccess().Can(WorkspacePermissions.ConversationsTakeOver))
        {
            IReadOnlyList<ConversationMessage> questions = await operations.OpenQuestionsAsync(
                Owner, HttpContext.SessionToken(), Reach, connectionId, sender, cancellationToken);
            if (questions.Count > 0) page = page with { Messages = [.. page.Messages.Concat(questions).OrderBy(m => m.ReceivedAt)] };
        }

        return Ok(mediaLinks.Sign(page));
    }

    // Closes an agent's question without a reply from here: the person dealt with it another way.
    [HttpPost("{connectionId:guid}/questions/{questionId}/done")]
    [RequirePermission(WorkspacePermissions.ConversationsTakeOver)]
    public async Task<IActionResult> MarkQuestionDone(Guid connectionId, string questionId, [FromQuery] string sender, CancellationToken cancellationToken)
    {
        HttpContext.RequireReach(connectionId);
        await operations.CloseQuestionAsync(Owner, HttpContext.SessionToken(), Reach, connectionId, sender, questionId, Reader, cancellationToken);
        return NoContent();
    }

    // Every file this customer has sent, newest first, for the customer details panel.
    [HttpGet("{connectionId:guid}/media")]
    public async Task<IActionResult> Media(Guid connectionId, [FromQuery] string sender,
        [FromQuery] string? before, CancellationToken cancellationToken)
    {
        await EnsureConnectionOwnedAsync(connectionId, cancellationToken);
        return Ok(mediaLinks.Sign(await store.ListMediaAsync(Owner, connectionId, sender, before, cancellationToken)));
    }

    [Monitor("conversations.send", SlowMs = 3000)] // waits on the WhatsApp Cloud API or TikTok
    [HttpPost("{connectionId:guid}/messages")]
    [RequirePermission(WorkspacePermissions.ConversationsReply)]
    public async Task<IActionResult> Send(Guid connectionId, [FromQuery] string sender,
        [FromBody] SendReplyRequest request, CancellationToken cancellationToken)
    {
        HttpContext.RequireReach(connectionId);
        return Ok(await service.SendAsync(Owner, connectionId, sender, request, cancellationToken));
    }

    // A picture or document for the customer, with an optional caption. The same request id makes a retry safe.
    [Monitor("conversations.send_file", SlowMs = 5000)]
    [HttpPost("{connectionId:guid}/attachments")]
    [RequestSizeLimit(ConversationService.MaxDocumentBytes + 1024 * 1024)]
    [EnableRateLimiting(RateLimitPolicies.FileSend)]
    [RequirePermission(WorkspacePermissions.ConversationsReply)]
    public async Task<IActionResult> SendFile(Guid connectionId, [FromQuery] string sender, [FromForm] SendFileForm form,
        CancellationToken cancellationToken)
    {
        HttpContext.RequireReach(connectionId);
        if (form.File is not { } upload) throw new ValidationException("Attach a file to send.");
        ConversationMessage sent = await service.SendFileAsync(Owner, connectionId, sender, form.RequestId, form.Caption,
            new OutgoingFile(upload.FileName, upload.ContentType, upload.Length, upload.OpenReadStream), cancellationToken, form.ReplyToMessageId);
        return Ok(mediaLinks.Sign(sent));
    }

    // Human takes over from the AI for a fixed duration or until they hand back.
    [HttpPost("{connectionId:guid}/human-takeover")]
    [RequirePermission(WorkspacePermissions.ConversationsReply)]
    public async Task<IActionResult> SetHumanTakeover(Guid connectionId, [FromQuery] string sender,
        [FromBody] HumanTakeoverRequest request, CancellationToken cancellationToken)
    {
        await EnsureConnectionOwnedAsync(connectionId, cancellationToken);
        DateTime? controlUntil = request.Minutes is { } m ? DateTime.UtcNow.AddMinutes(m) : null;
        await humanTakeover.SetAsync(Owner, connectionId, sender, controlUntil, cancellationToken);
        try
        {
            string eventText = controlUntil is { } until
                ? $"A team member took over for {request.Minutes} minute{(request.Minutes == 1 ? "" : "s")}."
                : "A team member took over the conversation.";
            await store.WriteSystemEventAsync(Owner, connectionId, sender, eventText, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "system_event_write_failed ConnectionId={ConnectionId} Sender={Sender} Event=takeover", connectionId, sender);
        }
        return Ok(new { ControlUntil = controlUntil });
    }

    // Hands the conversation to the AI: clears an active human takeover, or is a no-op when the AI
    // was already handling it. Safe to call on a cold-start conversation that has never had a takeover.
    [HttpDelete("{connectionId:guid}/human-takeover")]
    [RequirePermission(WorkspacePermissions.ConversationsReply)]
    public async Task<IActionResult> ClearHumanTakeover(Guid connectionId, [FromQuery] string sender,
        CancellationToken cancellationToken)
    {
        await EnsureConnectionOwnedAsync(connectionId, cancellationToken);
        await humanTakeover.ClearAsync(Owner, connectionId, sender, cancellationToken);
        try
        {
            await store.WriteSystemEventAsync(Owner, connectionId, sender, "The AI agent resumed the conversation.", cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "system_event_write_failed ConnectionId={ConnectionId} Sender={Sender} Event=return", connectionId, sender);
        }
        return NoContent();
    }

    // The person's pins and marks, without conversations on channels their grant does not reach.
    private async Task<IReadOnlyDictionary<(Guid Connection, string Sender), ConversationFlags>> ReachableFlagsAsync(CancellationToken ct)
    {
        IReadOnlyDictionary<(Guid Connection, string Sender), ConversationFlags> all = await flags.ListAsync(Reader, ct);
        return Reach is { } reach ? all.Where(f => reach.Contains(f.Key.Connection)).ToDictionary() : all;
    }

    // A thread can be on a WhatsApp number, a Facebook Page, the TikTok account or an Instagram account.
    // A channel the person's grant does not reach answers as not found, like one that does not exist.
    private async Task EnsureConnectionOwnedAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        HttpContext.RequireReach(connectionId);
        if (await connections.GetByOwnerAndIdAsync(Owner, connectionId) is null
            && !await facebook.IsOwnedByAsync(connectionId, Owner, cancellationToken)
            && (await tikTokConnections.GetByOwnerAsync(Owner, cancellationToken))?.Id != connectionId
            && !await instagram.IsOwnedByAsync(connectionId, Owner, cancellationToken))
            throw new NotFoundException("Connection not found.");
    }
}

/// <summary>How long a human wants to take over for. Null minutes means until they hand back manually.</summary>
public sealed record HumanTakeoverRequest(int? Minutes);

/// <summary>The multipart body of a file reply.</summary>
public sealed class SendFileForm
{
    public Guid RequestId { get; set; }
    public string? Caption { get; set; }
    public IFormFile? File { get; set; }

    /// <summary>The id of the message this file replies to (quotes); empty for a plain reply.</summary>
    public string? ReplyToMessageId { get; set; }
}
