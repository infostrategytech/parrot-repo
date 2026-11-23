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
/// </summary>
public sealed class AiOperationsService(
    IAgentActivityStore activity,
    IAgentRepository agents,
    IAgentOrchestratorClient orchestrator,
    IConversationStore conversations,
    TimeProvider clock,
    IAppLogger<AiOperationsService> logger) : IAiOperationsService
{
    public const int DefaultRecent = 20;
    public const int MaxRecent = 100;

    /// <summary>The message kind the inbox shows an agent's question as.</summary>
    public const string AiQuestionKind = "ai_question";

    private static readonly string[] TurnTypes = [AgentActivityTypes.TurnCompleted];
    private static readonly string[] RequestTypes = [AgentActivityTypes.ApprovalRequested, AgentActivityTypes.EscalationOpened];
    private static readonly string[] EscalationTypes = [AgentActivityTypes.EscalationOpened];

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
        string ownerUserId, IReadOnlySet<Guid>? reach, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<OpenItem> open = await OpenItemsAsync(ownerUserId, reach, cancellationToken);
        return open.Select(o => new WaitingItemResponse(
            o.AgentName,
            CustomerOf(o.Turn?.CustomerName ?? o.Record.CustomerName, o.Record.CustomerRef ?? o.Turn?.CustomerRef),
            QuestionOf(o.Record),
            o.Record.Type == AgentActivityTypes.ApprovalRequested ? o.Record.ApprovalId : null,
            o.Record.EscalationId,
            o.Record.Priority,
            o.Record.OccurredAt)
        {
            AgentId = o.AgentId,
            ConversationId = $"{o.ConnectionId}:{o.Sender}",
        }).ToList();
    }

    public async Task<IReadOnlyList<ConversationQuestions>> OpenByConversationAsync(
        string ownerUserId, IReadOnlySet<Guid>? reach, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<OpenItem> open = await OpenItemsAsync(ownerUserId, reach, cancellationToken);
        return open
            .GroupBy(o => (o.ConnectionId, o.Sender))
            .Select(conversation =>
            {
                // Oldest first, so the last is the newest: the agent that asked most recently is the one shown.
                OpenItem newest = conversation.Last();
                OpenItem? approval = conversation.LastOrDefault(o => o.Record.Type == AgentActivityTypes.ApprovalRequested);
                return new ConversationQuestions(
                    conversation.Key.ConnectionId,
                    conversation.Key.Sender,
                    conversation.Any(o => o.Record.Type == AgentActivityTypes.EscalationOpened),
                    approval is null ? null : new ConversationApproval(approval.Record.ApprovalId!, approval.Record.Tool, approval.Record.Reason),
                    newest.AgentId is { } agentId ? new ConversationAgentRef(agentId, newest.AgentName) : null);
            })
            .ToList();
    }

    /// <summary>An open approval or escalation, the turn that asked it, and the inbox conversation it belongs to.</summary>
    private sealed record OpenItem(AgentActivityRecord Record, AgentActivityRecord? Turn, string? AgentId, string AgentName, Guid ConnectionId, string Sender);

    private async Task<IReadOnlyList<OpenItem>> OpenItemsAsync(string ownerUserId, IReadOnlySet<Guid>? reach, CancellationToken cancellationToken)
    {
        IReadOnlyList<AgentActivityRecord> pending = await activity.ListPendingAsync(ownerUserId, cancellationToken);
        if (pending.Count == 0) return [];

        // An escalation opened because an approval timed out is the same question as the approval; show it once.
        HashSet<string> approvals = pending
            .Where(p => p.Type == AgentActivityTypes.ApprovalRequested && p.ApprovalId is not null)
            .Select(p => p.ApprovalId!)
            .ToHashSet(StringComparer.Ordinal);
        List<AgentActivityRecord> items = pending
            .Where(p => p.Type == AgentActivityTypes.ApprovalRequested
                || (p.Type == AgentActivityTypes.EscalationOpened && (p.ApprovalId is null || !approvals.Contains(p.ApprovalId))))
            .OrderBy(p => p.OccurredAt)
            .ToList();

        // The turn that asked tells us who the customer is and which agent was
        // answering. The lookup returns the asking rows too; only turn reports count.
        List<string> turnIds = items.Where(i => !string.IsNullOrEmpty(i.TurnId)).Select(i => i.TurnId!).Distinct().ToList();
        IReadOnlyList<AgentActivityRecord> related = turnIds.Count == 0
            ? []
            : await activity.ListByTurnIdsAsync(ownerUserId, turnIds, cancellationToken);
        Dictionary<string, AgentActivityRecord> turns = LatestPerTurn(related.Where(r => r.Type == AgentActivityTypes.TurnCompleted))
            .Where(t => t.TurnId is not null)
            .ToDictionary(t => t.TurnId!, StringComparer.Ordinal);
        // The conversation key names the conversation by its two participants; the stored messages say which inbox row that is.
        List<(AgentActivityRecord Item, AgentActivityRecord? Turn, string? Key)> asked = items.Select(item =>
        {
            AgentActivityRecord? turn = item.TurnId is not null && turns.TryGetValue(item.TurnId, out AgentActivityRecord? t) ? t : null;
            return (item, turn, item.ConversationKey ?? turn?.ConversationKey);
        }).ToList();
        List<string> keys = asked.Where(a => !string.IsNullOrWhiteSpace(a.Key)).Select(a => a.Key!).Distinct(StringComparer.Ordinal).ToList();
        IReadOnlyDictionary<string, (Guid Connection, string Sender)> conversationsByKey = keys.Count == 0
            ? new Dictionary<string, (Guid, string)>()
            : await conversations.FindByConversationKeysAsync(ownerUserId, keys, cancellationToken);

        IReadOnlyList<Agent> owned = await agents.GetByWorkspaceAsync(ownerUserId);
        Dictionary<string, string> names = owned.ToDictionary(a => a.Id.ToString(), a => a.Name, StringComparer.OrdinalIgnoreCase);
        string fallbackName = owned.Count == 1 ? owned[0].Name : "Your agent";

        List<OpenItem> open = [];
        int unlinked = 0;
        foreach ((AgentActivityRecord item, AgentActivityRecord? turn, string? key) in asked)
        {
            // Nothing in the inbox to answer it in (no key, or a chat Parrot never stored, such as a test): it can't be worked from here.
            if (key is null || !conversationsByKey.TryGetValue(key, out (Guid Connection, string Sender) conversation))
            {
                unlinked++;
                continue;
            }

            // A teammate limited to some channels never sees what customers asked on the others.
            if (reach is not null && !reach.Contains(conversation.Connection)) continue;

            string? agentId = turn?.AgentId ?? item.AgentId;
            string agentName = agentId is not null && names.TryGetValue(agentId, out string? name) ? name : fallbackName;
            open.Add(new OpenItem(item, turn, agentId, agentName, conversation.Connection, conversation.Sender));
        }

        if (unlinked > 0)
            logger.LogDebug("ai_operations.questions_unlinked OwnerId={OwnerId} Count={Count}", ownerUserId, unlinked);

        // A reply the team sent from the inbox after a question was asked answers it. An approval still needs its decision.
        List<(Guid, string)> asking = open.Where(o => o.Record.Type == AgentActivityTypes.EscalationOpened)
            .Select(o => (o.ConnectionId, o.Sender)).Distinct().ToList();
        if (asking.Count == 0) return open;
        IReadOnlyDictionary<(Guid Connection, string Sender), DateTime> replies =
            await conversations.LastTeamRepliesAsync(ownerUserId, asking, cancellationToken);
        return open.Where(o => o.Record.Type != AgentActivityTypes.EscalationOpened
            || !replies.TryGetValue((o.ConnectionId, o.Sender), out DateTime repliedAt)
            || repliedAt <= o.Record.OccurredAt).ToList();
    }

    public async Task<IReadOnlyList<ConversationMessage>> OpenQuestionsAsync(
        string ownerUserId, IReadOnlySet<Guid>? reach, Guid connectionId, string sender, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<OpenItem> open = await OpenItemsAsync(ownerUserId, reach, cancellationToken);
        return open
            .Where(o => o.Record.Type == AgentActivityTypes.EscalationOpened && o.ConnectionId == connectionId && o.Sender == sender)
            .Select(o => new ConversationMessage(o.Record.EscalationId!, QuestionOf(o.Record), false, "open", o.Record.OccurredAt)
            {
                Kind = AiQuestionKind,
            })
            .ToList();
    }

    public async Task CloseQuestionAsync(
        string ownerUserId, IReadOnlySet<Guid>? reach, Guid connectionId, string sender, string questionId, string closedBy,
        CancellationToken cancellationToken = default)
    {
        // Only a question still open in this very conversation: another workspace's, another chat's, or one already
        // answered all read as not found, the same as an id that never existed.
        IReadOnlyList<OpenItem> open = await OpenItemsAsync(ownerUserId, reach, cancellationToken);
        if (!open.Any(o => o.Record.Type == AgentActivityTypes.EscalationOpened && o.Record.EscalationId == questionId
            && o.ConnectionId == connectionId && o.Sender == sender))
            throw new NotFoundException("Question not found.");

        await activity.CloseEscalationAsync(ownerUserId, questionId, closedBy, clock.GetUtcNow().UtcDateTime, cancellationToken);
        logger.LogInformation("ai_operations.question_done OwnerId={OwnerId} QuestionId={QuestionId}", ownerUserId, questionId);

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

    private static string QuestionOf(AgentActivityRecord item) =>
        item.Type == AgentActivityTypes.ApprovalRequested
            ? (string.IsNullOrWhiteSpace(item.Reason) ? $"Approve {item.Tool ?? "this action"}?" : item.Reason)
            : (string.IsNullOrWhiteSpace(item.Problem) ? "Needs a person." : item.Problem);

    public async Task<ApprovalDecisionResponse> DecideApprovalAsync(
        string ownerUserId, string approvalId, ApprovalDecisionRequest request, CancellationToken cancellationToken = default)
    {
        string decision = request.Decision?.Trim().ToLowerInvariant() ?? string.Empty;
        if (decision is not (AgentActivityTypes.Resolution.Approved or AgentActivityTypes.Resolution.Denied))
            throw new ValidationException("Decide the approval with \"approved\" or \"denied\".");

        // The id is the owner's only when it is among the approvals asked of them; the body never says whose it is.
        IReadOnlyList<AgentActivityRecord> pending = await activity.ListPendingAsync(ownerUserId, cancellationToken);
        if (!pending.Any(p => p.Type == AgentActivityTypes.ApprovalRequested && p.ApprovalId == approvalId))
            throw new NotFoundException("No approval with that id is waiting for a decision.");

        ApprovalDecisionResult result = await orchestrator.DecideApprovalAsync(
            ownerUserId, approvalId, new ApprovalDecision(decision, ownerUserId, request.Instruction), cancellationToken);

        await activity.MarkApprovalDecidedAsync(
            ownerUserId, approvalId, decision, ownerUserId, request.Instruction, clock.GetUtcNow().UtcDateTime, cancellationToken);

        return new ApprovalDecisionResponse(approvalId, decision, result.TurnId, result.Status, result.Reply);
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
