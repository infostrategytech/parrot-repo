namespace Parrot.Web.Features.Dashboard.Tools;

public enum ToolRequestState { AwaitingApproval, Succeeded, Failed, Declined }

/// <summary>An action attached to an existing task, not a second queue or a payment/booking receipt.</summary>
public sealed record ToolActionRequest(Guid Id, string TaskId, Guid ConnectionId, string AgentId, string AgentName,
    string ToolId, string ActionId, string Description, ToolActionMode Mode, ToolRequestState State,
    bool IsTest, int Revision, string? Result = null, int ConnectionRevision = 0);

/// <summary>Explicit dry-run input. No real-mode adapter exists; never execute a provider from the browser.</summary>
public sealed record ToolActionTrial(Guid IdempotencyKey, Guid ConnectionId, string AgentId, string ActionId);

public interface IToolActionRequests
{
    bool CanTry { get; }
    Task<ToolActionRequest?> ForTaskAsync(string taskId, CancellationToken ct = default);
    Task<ToolActionRequest> TryAsync(ToolActionTrial trial, CancellationToken ct = default);
    Task<ToolActionRequest> DecideAsync(Guid id, bool approved, int expectedRevision, CancellationToken ct = default);
    Task<ToolActionRequest> RetryAsync(Guid id, int expectedRevision, CancellationToken ct = default);
}

public sealed class UnavailableToolActionRequests : IToolActionRequests
{
    public bool CanTry => false;
    public Task<ToolActionRequest?> ForTaskAsync(string taskId, CancellationToken ct = default) => Task.FromResult<ToolActionRequest?>(null);
    public Task<ToolActionRequest> TryAsync(ToolActionTrial trial, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ToolActionRequest> DecideAsync(Guid id, bool approved, int expectedRevision, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ToolActionRequest> RetryAsync(Guid id, int expectedRevision, CancellationToken ct = default) => throw new NotSupportedException();
}
