namespace Parrot.Application.Agents;

/// <summary>
/// One customer conversation an agent handed to a person, as the AI service keeps it. Repeated hand-overs in one
/// conversation are one item: <see cref="QuestionCount"/> says how many, and the newest one's words are kept.
/// </summary>
public sealed record WaitingListItem
{
    public const string OpenStatus = "open";

    /// <summary>The kind of an item that waits on <see cref="Approval"/>; the other kind, "handover", waits on a reply.</summary>
    public const string ApprovalKind = "approval";

    /// <summary>How an item someone marked handled is closed.</summary>
    public const string Handled = "handled";

    required public string Id { get; init; }

    /// <summary>"open" or "resolved".</summary>
    required public string Status { get; init; }

    /// <summary>"approval" or "handover".</summary>
    required public string Kind { get; init; }

    /// <summary>The agent that handed it over, by the AI service's id; null when it didn't know.</summary>
    public string? AgentId { get; init; }

    public string? CustomerRef { get; init; }

    public string? CustomerName { get; init; }

    /// <summary>The conversation key Parrot sent on messages.persisted; it finds the inbox conversation.</summary>
    public string? ConversationKey { get; init; }

    /// <summary>The agent's note for the team. Always there.</summary>
    required public string Summary { get; init; }

    /// <summary>P1 (most urgent), P2 or P3.</summary>
    public string? Priority { get; init; }

    public int QuestionCount { get; init; }

    /// <summary>The newest hand-over's escalation id.</summary>
    public string? EscalationId { get; init; }

    /// <summary>When the first hand-over opened it.</summary>
    public DateTime OpenedAt { get; init; }

    /// <summary>When the newest hand-over arrived.</summary>
    public DateTime LastAskedAt { get; init; }

    public WaitingListApproval? Approval { get; init; }

    /// <summary>How it closed (handled, team_replied, approved or denied); null while open.</summary>
    public string? Resolution { get; init; }

    /// <summary>The person who closed it; null while open, and when a team reply closed it.</summary>
    public string? ResolvedBy { get; init; }
}

/// <summary>The action an item's agent asked permission for.</summary>
public sealed record WaitingListApproval(string Id, string? Tool, string? Reason, string Status)
{
    /// <summary>Can still be decided; the others are approved, denied and expired.</summary>
    public const string PendingStatus = "pending";
}

/// <summary>
/// The AI service's waiting list (/api/v1/ai-operations/waiting): it opens, merges and closes the items itself.
/// Every call is made as the signed-in person, with their own session token: the service reads the workspace from
/// it, and records the person when they close an item. It doesn't limit what a member sees; callers do.
/// </summary>
public interface IWaitingListClient
{
    /// <summary>The workspace's open items, longest-waiting first, up to 1,000.</summary>
    Task<IReadOnlyList<WaitingListItem>> ListOpenAsync(string userToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// One item, open or closed. Throws <see cref="Domain.Exceptions.NotFoundException"/> when the workspace has no
    /// such item, which is also the answer for another workspace's.
    /// </summary>
    Task<WaitingListItem> GetAsync(string userToken, string itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the item handled as the token's person and returns it as it now is. One already closed, however it
    /// closed, comes back unchanged. Throws <see cref="Domain.Exceptions.NotFoundException"/> as <see cref="GetAsync"/> does.
    /// </summary>
    Task<WaitingListItem> ResolveAsync(string userToken, string itemId, CancellationToken cancellationToken = default);
}
