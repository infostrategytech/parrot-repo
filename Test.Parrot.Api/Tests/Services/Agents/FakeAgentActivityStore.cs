using Parrot.Application.Agents;

namespace Test.Parrot.Api.Tests.Services.Agents;

/// <summary>An in-memory activity store with the unique (type, key) rule; set <see cref="Failure"/> to make every call throw, as MongoDB down would.</summary>
public sealed class FakeAgentActivityStore : IAgentActivityStore
{
    private static readonly string[] PendingTypes = [AgentActivityTypes.ApprovalRequested, AgentActivityTypes.EscalationOpened];

    public List<AgentActivityRecord> Records { get; } = [];

    public Exception? Failure { get; set; }

    public int Inserts { get; private set; }

    public Task<bool> InsertAsync(AgentActivityRecord record, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        if (Records.Any(r => r.Type == record.Type && r.EventKey == record.EventKey)) return Task.FromResult(false);
        Records.Add(record);
        Inserts++;
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<AgentActivityRecord>> ListAsync(
        string ownerUserId, IReadOnlyCollection<string> types, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        IReadOnlyList<AgentActivityRecord> list = Records
            .Where(r => r.OwnerUserId == ownerUserId && types.Contains(r.Type) && r.OccurredAt >= fromUtc && r.OccurredAt < toUtc)
            .OrderBy(r => r.OccurredAt)
            .ToList();
        return Task.FromResult(list);
    }

    public Task<IReadOnlyList<AgentActivityRecord>> ListRecentAsync(string ownerUserId, string type, int limit, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        IReadOnlyList<AgentActivityRecord> list = Records
            .Where(r => r.OwnerUserId == ownerUserId && r.Type == type)
            .OrderByDescending(r => r.OccurredAt)
            .Take(limit)
            .ToList();
        return Task.FromResult(list);
    }

    public Task<IReadOnlyList<AgentActivityRecord>> ListByTurnIdsAsync(
        string ownerUserId, IReadOnlyCollection<string> turnIds, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        IReadOnlyList<AgentActivityRecord> list = Records
            .Where(r => r.OwnerUserId == ownerUserId && r.TurnId is not null && turnIds.Contains(r.TurnId))
            .OrderBy(r => r.OccurredAt)
            .ToList();
        return Task.FromResult(list);
    }

    public Task<IReadOnlyList<AgentActivityRecord>> ListPendingAsync(string ownerUserId, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        IReadOnlyList<AgentActivityRecord> list = Records
            .Where(r => r.OwnerUserId == ownerUserId && PendingTypes.Contains(r.Type) && r.ResolvedAt is null)
            .OrderBy(r => r.OccurredAt)
            .ToList();
        return Task.FromResult(list);
    }

    public Task<int> MarkApprovalDecidedAsync(
        string ownerUserId,
        string approvalId,
        string resolution,
        string decidedBy,
        string? instruction,
        DateTime decidedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        int touched = 0;
        foreach (AgentActivityRecord record in Records.Where(r => r.OwnerUserId == ownerUserId && r.ApprovalId == approvalId && r.ResolvedAt is null))
        {
            record.ResolvedAt = decidedAtUtc;
            record.Resolution = resolution;
            record.ResolvedBy = decidedBy;
            record.Instruction = instruction;
            touched++;
        }

        return Task.FromResult(touched);
    }

    private void ThrowIfFailing()
    {
        if (Failure is not null) throw Failure;
    }
}

/// <summary>A clock that stands still, so "today" is the same day for the whole test.</summary>
public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
