namespace Parrot.Domain.Entities;

/// <summary>
/// One row per workspace. Holds the monotonically increasing snapshot version that
/// orders all BusinessSourcesUpdated events for the workspace. Never resets.
/// Written inside the same transaction as every source mutation so the version and
/// the outbox event are always consistent.
/// </summary>
public class WorkspaceSourceState
{
    /// <summary>PK. string — matches WorkspaceId type on all connection entities.</summary>
    public required string WorkspaceId { get; set; }

    /// <summary>
    /// Increments on every change to the workspace's active source set or metadata.
    /// Omni uses this — not <c>occurred_at</c> — to determine which snapshot is latest.
    /// </summary>
    public long SnapshotVersion { get; set; }

    public DateTime UpdatedAt { get; set; }
}
