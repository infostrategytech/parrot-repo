using Microsoft.Extensions.Caching.Memory;
using Parrot.Application.Contracts.Conversations;
using Parrot.Application.Conversations;
using Parrot.Application.Logging;
using Parrot.Domain.Entities;
using Parrot.Domain.Exceptions;

namespace Parrot.Application.Agents;

/// <summary>
/// Works the AI Operations figures out from the activity rows each time they
/// are asked for. Days are UTC days. A turn counts once, by the last status
/// it reported: a turn that waited for an approval reports twice, and the
/// second report is the one that says how it ended.
///
/// What is waiting for the team is the AI service's waiting list: it opens, merges and closes the items itself.
/// This service limits it to the inbox conversations the caller can open.
/// </summary>
public sealed class AiOperationsService(
    IAgentActivityStore activity,
    IAgentRepository agents,
    IAgentOrchestratorClient orchestrator,
    IWaitingListClient waitingList,
    IConversationStore conversations,
    IHumanTakeoverStore humanTakeover,
    IMemoryCache cache,
    TimeProvider clock,
    IAppLogger<AiOperationsService> logger) : IAiOperationsService
{
    public const int DefaultRecent = 20;
    public const int MaxRecent = 100;

    /// <summary>The message kind the inbox shows an agent's question as.</summary>
    public const string AiQuestionKind = "ai_question";

    /// <summary>How long the waiting list is shared before it is read again: a new hand-over shows within this, plus the inbox's own refresh.</summary>
    public static readonly TimeSpan WaitingFreshFor = TimeSpan.FromSeconds(10);

    /// <summary>How long to wait before asking again when the AI service didn't answer.</summary>
    public static readonly TimeSpan WaitingRetryAfter = TimeSpan.FromSeconds(30);

    /// <summary>The AI service's limit for what the person tells the agent with a decision.</summary>
    public const int MaxInstruction = 1000;

    /// <summary>The longest answer to an agent's question, the same as a decision's instruction.</summary>
    public const int MaxAnswer = 1000;

    private const string QuestionNotFound = "Question not found.";
    private const string ApprovalNotFound = "No approval with that id is waiting for a decision.";

    private static readonly string[] TurnTypes = [AgentActivityTypes.TurnCompleted];
    private static readonly string[] RequestTypes = [AgentActivityTypes.ApprovalRequested, AgentActivityTypes.EscalationOpened];

    public async Task<AgentTodayResponse> TodayAsync(string ownerUserId, string? agentId, string? channel, CancellationToken cancellationToken = default)
    {
        DateTime now = clock.GetUtcNow().UtcDateTime;
        DateTime dayStart = now.Date;
        DateTime dayEnd = dayStart.AddDays(1);

        IReadOnlyList<AgentActivityRecord> turns = await activity.ListAsync(ownerUserId, TurnTypes, dayStart, dayEnd, cancellationToken);
        // A request decided today may have been opened days ago; the week is as far back as one waits.
        IReadOnlyList<AgentActivityRecord> requests = await activity.ListAsync(ownerUserId, RequestTypes, dayStart.AddDays(-7), dayEnd, cancellationToken);

        return Today(turns, requests, dayStart, agentId, channel);
    }

    public async Task<IReadOnlyList<int>> WeekAsync(string ownerUserId, string? agentId, string? channel, CancellationToken cancellationToken = default)
    {
        DateTime todayStart = clock.GetUtcNow().UtcDateTime.Date;
        IReadOnlyList<AgentActivityRecord> turns = await activity.ListAsync(
            ownerUserId, TurnTypes, todayStart.AddDays(-6), todayStart.AddDays(1), cancellationToken);

        return Week(turns, todayStart, agentId, channel);
    }

    public async Task<IReadOnlyList<RecentConversationResponse>> RecentAsync(
        string ownerUserId, string? agentId, string? channel, int limit, CancellationToken cancellationToken = default)
    {
        int take = Math.Clamp(limit, 1, MaxRecent);
        DateTime now = clock.GetUtcNow().UtcDateTime;

        // Read more than a page: a turn can have two reports, and a filter drops some.
        IReadOnlyList<AgentActivityRecord> turns = await activity.ListRecentAsync(ownerUserId, AgentActivityTypes.TurnCompleted, take * 4, cancellationToken);
        List<AgentActivityRecord> latest = LatestPerTurn(turns)
            .Where(t => Matches(t, agentId, channel))
            .OrderByDescending(t => t.OccurredAt)
            .Take(take)
            .ToList();

        Dictionary<string, string?> problems = [];
        List<string> escalated = latest.Where(t => !string.IsNullOrEmpty(t.EscalationId)).Select(t => t.TurnId!).ToList();
        if (escalated.Count > 0)
        {
            foreach (AgentActivityRecord escalation in await activity.ListByTurnIdsAsync(ownerUserId, escalated, cancellationToken))
            {
                if (escalation.Type == AgentActivityTypes.EscalationOpened && escalation.EscalationId is { } id)
                    problems[id] = escalation.Problem;
            }
        }

        return latest.Select(t => new RecentConversationResponse(
            CustomerOf(t.CustomerName, t.CustomerRef),
            t.Channel ?? "whatsapp",
            OutcomeOf(t),
            t.EscalationId is { } id && problems.TryGetValue(id, out string? problem) ? problem : null,
            Math.Max(0, (int)(now - t.OccurredAt).TotalMinutes),
            t.TurnId ?? string.Empty,
            t.ConversationKey ?? string.Empty)).ToList();
    }

    public async Task<IReadOnlyList<WaitingItemResponse>> WaitingAsync(
        string ownerUserId, string userToken, IReadOnlySet<Guid>? reach, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<OpenItem> open = await OpenItemsAsync(ownerUserId, userToken, reach, cancellationToken);
        return open.Select(o => new WaitingItemResponse(
            o.AgentName,
            CustomerOf(o.Item.CustomerName, o.Item.CustomerRef),
            QuestionOf(o.Item),
            PendingApprovalOf(o.Item)?.ApprovalId,
            o.Item.EscalationId,
            o.Item.Priority,
            o.Item.OpenedAt)
        {
            AgentId = o.Item.AgentId,
            ConversationId = $"{o.ConnectionId}:{o.Sender}",
        }).ToList();
    }

    public async Task<IReadOnlyList<ConversationQuestions>> OpenByConversationAsync(
        string ownerUserId, string userToken, IReadOnlySet<Guid>? reach, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<OpenItem> open = await OpenItemsAsync(ownerUserId, userToken, reach, cancellationToken);

        // The AI service keeps one open item per conversation; grouping only guards against it sending two.
        return open
            .GroupBy(o => (o.ConnectionId, o.Sender))
            .Select(conversation =>
            {
                OpenItem newest = conversation.OrderBy(o => o.Item.LastAskedAt).Last();
                ConversationApproval? approval = conversation.Select(o => PendingApprovalOf(o.Item)).LastOrDefault(a => a is not null);
                return new ConversationQuestions(
                    conversation.Key.ConnectionId,
                    conversation.Key.Sender,
                    conversation.Any(o => NeedsAnswer(o.Item)),
                    approval,
                    newest.Item.AgentId is { } agentId ? new ConversationAgentRef(agentId, newest.AgentName) : null);
            })
            .ToList();
    }

    public async Task<IReadOnlyList<ConversationMessage>> OpenQuestionsAsync(
        string ownerUserId, string userToken, IReadOnlySet<Guid>? reach, Guid connectionId, string sender, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<OpenItem> open = await OpenItemsAsync(ownerUserId, userToken, reach, cancellationToken);

        // Placed at the newest hand-over, so a team reply sent after it reads as its answer, as the AI service reads it.
        return open
            .Where(o => o.ConnectionId == connectionId && o.Sender == sender && NeedsAnswer(o.Item))
            .Select(o => new ConversationMessage(o.Item.Id, QuestionOf(o.Item), false, "open", o.Item.LastAskedAt)
            {
                Kind = AiQuestionKind,
            })
            .ToList();
    }

    public async Task CloseQuestionAsync(
        string ownerUserId, string userToken, IReadOnlySet<Guid>? reach, Guid connectionId, string sender, string questionId, string closedBy,
        CancellationToken cancellationToken = default)
    {
        await RequireOpenQuestionAsync(ownerUserId, userToken, reach, connectionId, sender, questionId, cancellationToken);

        WaitingListItem closed;
        try
        {
            closed = await waitingList.ResolveAsync(userToken, questionId, cancellationToken);
        }
        catch (NotFoundException)
        {
            throw new NotFoundException(QuestionNotFound);
        }

        cache.Remove(WaitingKey(ownerUserId));

        // Someone else, or a team reply, closed it between the read and the write: the first close wins, and it is
        // closed either way. Only the person whose close it was says so in the chat.
        bool closedHere = closed.Resolution == WaitingListItem.Handled && closed.ResolvedBy == closedBy;
        logger.LogInformation("ai_operations.question_done OwnerId={OwnerId} QuestionId={QuestionId} ClosedHere={ClosedHere}", ownerUserId, questionId, closedHere);
        if (!closedHere) return;

        // Best effort: the question is closed either way, and the line in the chat only says so.
        try
        {
            await conversations.WriteSystemEventAsync(ownerUserId, connectionId, sender, "A team member marked the agent's question as done.", cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "system_event_write_failed ConnectionId={ConnectionId} Event=question_done", connectionId);
        }
    }

    public async Task<QuestionAnswerResponse> AnswerQuestionAsync(
        string ownerUserId, string userToken, IReadOnlySet<Guid>? reach, Guid connectionId, string sender, string questionId,
        QuestionAnswerRequest request, CancellationToken cancellationToken = default)
    {
        string answer = request.Answer?.Trim() ?? string.Empty;
        if (answer.Length == 0)
            throw new ValidationException("Write your answer for the agent.");
        if (answer.Length > MaxAnswer)
            throw new ValidationException("Keep your answer to 1,000 characters.");

        await RequireOpenQuestionAsync(ownerUserId, userToken, reach, connectionId, sender, questionId, cancellationToken);

        // With a person in the chat, the agent learns the answer but leaves the reply to them.
        bool agentReplies = !await humanTakeover.IsActiveAsync(ownerUserId, connectionId, sender, cancellationToken);
        try
        {
            await waitingList.AnswerAsync(userToken, questionId, new WaitingListAnswer(answer, agentReplies, request.SaveForNextTime), cancellationToken);
        }
        catch (NotFoundException)
        {
            throw new NotFoundException(QuestionNotFound);
        }

        cache.Remove(WaitingKey(ownerUserId));
        logger.LogInformation(
            "ai_operations.question_answered OwnerId={OwnerId} QuestionId={QuestionId} AgentReplies={AgentReplies} SaveForNextTime={SaveForNextTime}",
            ownerUserId, questionId, agentReplies, request.SaveForNextTime);

        // Best effort: the agent has the answer either way, and the line in the chat only says so.
        try
        {
            await conversations.WriteSystemEventAsync(ownerUserId, connectionId, sender, "A team member answered the agent's question.", cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "system_event_write_failed ConnectionId={ConnectionId} Event=question_answered", connectionId);
        }

        return new QuestionAnswerResponse(agentReplies);
    }

    // Read afresh, not from the cache: only a question still open in this very conversation, on a channel the person
    // reaches. One in another chat, one already closed, another workspace's and one that never existed all read as
    // not found, the same.
    private async Task RequireOpenQuestionAsync(
        string ownerUserId, string userToken, IReadOnlySet<Guid>? reach, Guid connectionId, string sender, string questionId,
        CancellationToken cancellationToken)
    {
        WaitingListItem item;
        try
        {
            item = await waitingList.GetAsync(userToken, questionId, cancellationToken);
        }
        catch (NotFoundException)
        {
            throw new NotFoundException(QuestionNotFound);
        }

        if (item.Status != WaitingListItem.OpenStatus || !NeedsAnswer(item) || (reach is not null && !reach.Contains(connectionId)))
            throw new NotFoundException(QuestionNotFound);
        IReadOnlyList<OpenItem> linked = await LinkAsync(ownerUserId, [item], cancellationToken);
        if (!linked.Any(o => o.ConnectionId == connectionId && o.Sender == sender))
            throw new NotFoundException(QuestionNotFound);
    }

    /// <summary>An open item on the AI service's waiting list, the inbox conversation it belongs to, and the agent that asked.</summary>
    private sealed record OpenItem(WaitingListItem Item, Guid ConnectionId, string Sender, string AgentName);

    // The workspace's open items, read from the AI service at most once per WaitingFreshFor and shared by everyone in
    // the workspace: the inbox re-reads on a timer in every open tab, and the service allows 120 reads a minute.
    private async Task<IReadOnlyList<OpenItem>> OpenItemsAsync(string ownerUserId, string userToken, IReadOnlySet<Guid>? reach, CancellationToken cancellationToken)
    {
        IReadOnlyList<OpenItem> all = await cache.GetOrCreateAsync(WaitingKey(ownerUserId), async entry =>
        {
            IReadOnlyList<WaitingListItem> items;
            try
            {
                items = await waitingList.ListOpenAsync(userToken, cancellationToken);
            }
            catch (ServiceUnavailableException exception)
            {
                // Nothing shows as waiting rather than the inbox failing; asked again once the service has had time to recover.
                entry.AbsoluteExpirationRelativeToNow = WaitingRetryAfter;
                logger.LogWarning(exception, "ai_operations.waiting_unavailable OwnerId={OwnerId}: nothing shows as waiting until it answers", ownerUserId);
                return [];
            }

            entry.AbsoluteExpirationRelativeToNow = WaitingFreshFor;
            return await LinkAsync(ownerUserId, items, cancellationToken);
        }) ?? [];

        // A teammate limited to some channels never sees what customers asked on the others.
        return reach is null ? all : all.Where(o => reach.Contains(o.ConnectionId)).ToList();
    }

    // The conversation key names the conversation by its two participants; the owner's stored messages say which inbox
    // row that is now (a channel connected again gets a new connection). An item with no such row, such as one in a chat
    // Parrot never stored, can't be worked from the inbox and is left out.
    private async Task<IReadOnlyList<OpenItem>> LinkAsync(string ownerUserId, IReadOnlyList<WaitingListItem> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0) return [];
        List<string> keys = items.Where(i => !string.IsNullOrWhiteSpace(i.ConversationKey)).Select(i => i.ConversationKey!).Distinct(StringComparer.Ordinal).ToList();
        IReadOnlyDictionary<string, (Guid Connection, string Sender)> conversationsByKey = keys.Count == 0
            ? new Dictionary<string, (Guid, string)>()
            : await conversations.FindByConversationKeysAsync(ownerUserId, keys, cancellationToken);

        IReadOnlyList<Agent> owned = await agents.GetByWorkspaceAsync(ownerUserId);
        Dictionary<string, string> names = owned.ToDictionary(a => a.Id.ToString(), a => a.Name, StringComparer.OrdinalIgnoreCase);
        string fallbackName = owned.Count == 1 ? owned[0].Name : "Your agent";

        List<OpenItem> open = [];
        foreach (WaitingListItem item in items)
        {
            if (item.ConversationKey is not { } key || !conversationsByKey.TryGetValue(key, out (Guid Connection, string Sender) conversation)) continue;
            string agentName = item.AgentId is not null && names.TryGetValue(item.AgentId, out string? name) ? name : fallbackName;
            open.Add(new OpenItem(item, conversation.Connection, conversation.Sender, agentName));
        }

        if (open.Count < items.Count)
            logger.LogDebug("ai_operations.questions_unlinked OwnerId={OwnerId} Count={Count}", ownerUserId, items.Count - open.Count);
        return open;
    }

    private static string WaitingKey(string ownerUserId) => $"ai_waiting:{ownerUserId}";

    // An approval the agent still waits on. One that expired can't be decided any more; the AI service then calls the item a hand-over.
    private static ConversationApproval? PendingApprovalOf(WaitingListItem item) =>
        item.Kind == WaitingListItem.ApprovalKind && item.Approval is { Status: WaitingListApproval.PendingStatus } approval
            ? new ConversationApproval(approval.Id, approval.Tool, approval.Reason)
            : null;

    // A hand-over needs someone to answer the customer; so does an approval the customer has asked more since (each later
    // hand-over in the conversation joins its item).
    private static bool NeedsAnswer(WaitingListItem item) => PendingApprovalOf(item) is null || item.QuestionCount > 1;

    // The agent's note for the team; the customer's own words are already in the chat.
    private static string QuestionOf(WaitingListItem item) => string.IsNullOrWhiteSpace(item.Summary) ? "Needs a person." : item.Summary;

    public async Task<ApprovalDecisionResponse> DecideApprovalAsync(
        string ownerUserId, string userToken, IReadOnlySet<Guid>? reach, string decidedBy, string approvalId, ApprovalDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        string decision = request.Decision?.Trim().ToLowerInvariant() ?? string.Empty;
        if (decision is not (AgentActivityTypes.Resolution.Approved or AgentActivityTypes.Resolution.Denied))
            throw new ValidationException("Decide the approval with \"approved\" or \"denied\".");
        string? instruction = string.IsNullOrWhiteSpace(request.Instruction) ? null : request.Instruction.Trim();
        if (instruction?.Length > MaxInstruction)
            throw new ValidationException("Keep the instruction for the agent to 1,000 characters.");

        // Only an approval still waiting on an open item in this workspace, on a channel the person reaches. The body
        // never says whose it is, or whether it exists elsewhere.
        if (!await IsApprovalWaitingAsync(ownerUserId, userToken, reach, approvalId, cancellationToken))
            throw new NotFoundException(ApprovalNotFound);

        ApprovalDecisionResult result;
        try
        {
            result = await orchestrator.DecideApprovalAsync(
                ownerUserId, approvalId, new ApprovalDecision(decision, decidedBy, instruction), cancellationToken);
        }
        catch (NotFoundException)
        {
            throw new NotFoundException(ApprovalNotFound);
        }

        // The decision closes the approval's waiting item, or leaves it waiting on an answer when the customer has asked
        // more since; the next read shows that rather than the shared copy.
        cache.Remove(WaitingKey(ownerUserId));
        logger.LogInformation("ai_operations.approval_decided OwnerId={OwnerId} ApprovalId={ApprovalId} Decision={Decision} Status={Status}",
            ownerUserId, approvalId, decision, result.Status);

        // Someone else's decision came first and stands: there is nothing of this person's to record.
        if (result.Status != ApprovalDecisionStatus.AlreadyDecided)
        {
            try
            {
                await activity.MarkApprovalDecidedAsync(
                    ownerUserId, approvalId, decision, decidedBy, instruction, clock.GetUtcNow().UtcDateTime, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Best effort: the AI service has the decision, and that is what the agent acts on. Parrot's copy only
                // feeds the day's reply-time figure.
                logger.LogWarning(exception, "ai_operations.approval_record_failed OwnerId={OwnerId} ApprovalId={ApprovalId}", ownerUserId, approvalId);
            }
        }

        return new ApprovalDecisionResponse(approvalId, decision, result.TurnId, result.Status, result.Reply);
    }

    // In the shared copy or, failing that, read afresh: the chat can show an approval from the agent's live event a few
    // seconds before the copy is read again, and an empty copy may only mean the AI service didn't answer last time.
    // A read that fails now says so, rather than reading as "not waiting".
    private async Task<bool> IsApprovalWaitingAsync(
        string ownerUserId, string userToken, IReadOnlySet<Guid>? reach, string approvalId, CancellationToken cancellationToken)
    {
        bool Waits(OpenItem open) => PendingApprovalOf(open.Item)?.ApprovalId == approvalId;
        if ((await OpenItemsAsync(ownerUserId, userToken, reach, cancellationToken)).Any(Waits)) return true;

        IReadOnlyList<OpenItem> fresh = await LinkAsync(ownerUserId, await waitingList.ListOpenAsync(userToken, cancellationToken), cancellationToken);
        cache.Set(WaitingKey(ownerUserId), fresh, WaitingFreshFor);
        return fresh.Any(o => (reach is null || reach.Contains(o.ConnectionId)) && Waits(o));
    }

    /// <summary>Today's figures from the day's turn reports and the requests decided in it.</summary>
    public static AgentTodayResponse Today(
        IEnumerable<AgentActivityRecord> turns, IEnumerable<AgentActivityRecord> requests, DateTime dayStartUtc, string? agentId, string? channel)
    {
        DateTime dayEnd = dayStartUtc.AddDays(1);
        List<AgentActivityRecord> latest = LatestPerTurn(turns)
            .Where(t => t.OccurredAt >= dayStartUtc && t.OccurredAt < dayEnd && Matches(t, agentId, channel))
            .ToList();
        Dictionary<string, AgentActivityRecord> byTurn = latest.Where(t => t.TurnId is not null).ToDictionary(t => t.TurnId!, StringComparer.Ordinal);

        int conversations = latest.Select(t => t.ConversationKey ?? t.TurnId ?? string.Empty).Distinct(StringComparer.Ordinal).Count();
        int answered = latest.Count(t => t.Status == AgentActivityTypes.TurnStatus.Completed);
        int passed = latest.Count(t => t.Status is AgentActivityTypes.TurnStatus.Escalated
            or AgentActivityTypes.TurnStatus.AwaitingApproval
            or AgentActivityTypes.TurnStatus.Failed);
        int tasks = latest.Count(t => !string.IsNullOrEmpty(t.EscalationId));

        List<double> waits = requests
            .Where(r => r.ResolvedAt is { } at && at >= dayStartUtc && at < dayEnd && MatchesRequest(r, byTurn, agentId, channel))
            .Select(r => Math.Max(0, (r.ResolvedAt!.Value - r.OccurredAt).TotalMinutes))
            .ToList();
        int? reply = waits.Count == 0 ? null : (int)Math.Round(waits.Average());

        return new AgentTodayResponse(conversations, answered, passed, tasks, reply);
    }

    /// <summary>Conversations per day for the seven days ending today, oldest first.</summary>
    public static IReadOnlyList<int> Week(IEnumerable<AgentActivityRecord> turns, DateTime todayStartUtc, string? agentId, string? channel)
    {
        List<AgentActivityRecord> latest = LatestPerTurn(turns).Where(t => Matches(t, agentId, channel)).ToList();
        int[] days = new int[7];
        for (int i = 0; i < days.Length; i++)
        {
            DateTime start = todayStartUtc.AddDays(i - 6);
            DateTime end = start.AddDays(1);
            days[i] = latest
                .Where(t => t.OccurredAt >= start && t.OccurredAt < end)
                .Select(t => t.ConversationKey ?? t.TurnId ?? string.Empty)
                .Distinct(StringComparer.Ordinal)
                .Count();
        }

        return days;
    }

    /// <summary>One report per turn, the latest; turns the orchestrator skipped are not activity.</summary>
    public static List<AgentActivityRecord> LatestPerTurn(IEnumerable<AgentActivityRecord> turns) =>
        turns
            .Where(t => t.Status != AgentActivityTypes.TurnStatus.Skipped)
            .GroupBy(t => t.TurnId ?? t.EventKey, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(t => t.OccurredAt).First())
            .ToList();

    private static bool Matches(AgentActivityRecord turn, string? agentId, string? channel) =>
        (string.IsNullOrWhiteSpace(agentId) || string.Equals(turn.AgentId, agentId, StringComparison.OrdinalIgnoreCase))
        && (string.IsNullOrWhiteSpace(channel) || string.Equals(turn.Channel, channel, StringComparison.OrdinalIgnoreCase));

    // A request names its channel but not its agent; the turn it belongs to names both.
    private static bool MatchesRequest(AgentActivityRecord request, Dictionary<string, AgentActivityRecord> turns, string? agentId, string? channel)
    {
        AgentActivityRecord? turn = request.TurnId is not null && turns.TryGetValue(request.TurnId, out AgentActivityRecord? t) ? t : null;
        bool channelOk = string.IsNullOrWhiteSpace(channel)
            || string.Equals(request.Channel ?? turn?.Channel, channel, StringComparison.OrdinalIgnoreCase);
        bool agentOk = string.IsNullOrWhiteSpace(agentId)
            || (turn is not null && string.Equals(turn.AgentId, agentId, StringComparison.OrdinalIgnoreCase));
        return channelOk && agentOk;
    }

    private static string OutcomeOf(AgentActivityRecord turn) =>
        turn.Status == AgentActivityTypes.TurnStatus.Completed ? ConversationOutcomes.Answered
        : !string.IsNullOrEmpty(turn.EscalationId) ? ConversationOutcomes.TaskCreated
        : ConversationOutcomes.PassedToTeam;

    private static string CustomerOf(string? name, string? reference) =>
        !string.IsNullOrWhiteSpace(name) ? name
        : !string.IsNullOrWhiteSpace(reference) ? reference
        : "A customer";
}
