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
    TimeProvider clock) : IAiOperationsService
{
    public const int DefaultRecent = 20;
    public const int MaxRecent = 100;

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

    public async Task<IReadOnlyList<WaitingItemResponse>> WaitingAsync(string ownerUserId, CancellationToken cancellationToken = default)
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
        IReadOnlyList<Agent> owned = await agents.GetByUserIdAsync(ownerUserId);
        Dictionary<string, string> names = owned.ToDictionary(a => a.Id.ToString(), a => a.Name, StringComparer.OrdinalIgnoreCase);
        string fallbackName = owned.Count == 1 ? owned[0].Name : "Your agent";

        return items.Select(item =>
        {
            AgentActivityRecord? turn = item.TurnId is not null && turns.TryGetValue(item.TurnId, out AgentActivityRecord? t) ? t : null;
            string agentName = turn?.AgentId is { } agentId && names.TryGetValue(agentId, out string? name) ? name : fallbackName;
            string question = item.Type == AgentActivityTypes.ApprovalRequested
                ? (string.IsNullOrWhiteSpace(item.Reason) ? $"Approve {item.Tool ?? "this action"}?" : item.Reason)
                : (string.IsNullOrWhiteSpace(item.Problem) ? "Needs a person." : item.Problem);
            return new WaitingItemResponse(
                agentName,
                CustomerOf(turn?.CustomerName ?? item.CustomerName, item.CustomerRef ?? turn?.CustomerRef),
                question,
                item.Type == AgentActivityTypes.ApprovalRequested ? item.ApprovalId : null,
                item.EscalationId,
                item.Priority,
                item.OccurredAt);
        }).ToList();
    }

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
