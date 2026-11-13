namespace Parrot.Application.BusinessSources;

/// <summary>
/// Builds and enqueues a BusinessSourcesUpdated outbox event for the workspace.
/// Must be called within an open database transaction, AFTER any source mutations
/// have been persisted with SaveChanges. Increments WorkspaceSourceState.SnapshotVersion,
/// builds the full active-source snapshot, and writes the OutboxEvent row — then calls
/// SaveChanges. The caller is responsible for the outer transaction commit.
/// </summary>
public interface ISnapshotPublisher
{
    Task PublishAsync(string workspaceId, CancellationToken ct = default);
}
