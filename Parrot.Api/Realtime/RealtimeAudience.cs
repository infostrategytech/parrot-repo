using System.Collections.Concurrent;
using Parrot.Application.Teams;

namespace Parrot.Api.Realtime;

/// <summary>Narrows a push to the connections whose person may see it. See <see cref="RealtimeAudience"/>.</summary>
public interface IRealtimeAudience
{
    /// <summary>The connections whose person's access in the workspace passes <paramref name="allowed"/>.</summary>
    Task<IReadOnlyList<string>> FilterAsync(string workspaceId, IReadOnlyList<string> connections, Func<WorkspaceAccess, bool> allowed);

    /// <summary>For a conversation event: someone who may read conversations, on that connection's channel.</summary>
    Task<IReadOnlyList<string>> ForConversationAsync(string workspaceId, IReadOnlyList<string> connections, Guid? connectionId) =>
        FilterAsync(workspaceId, connections, a =>
            a.Can(WorkspacePermissions.ConversationsView) && (connectionId is { } id ? a.CanReach(id) : a.AllChannels));
}

/// <summary>
/// Who may receive a push. A workspace's live connections are everyone working
/// in it, and a team member may be limited to some channels or not allowed to
/// read conversations at all, so each push keeps only the connections whose
/// person may see it. <see cref="MessagingHub"/> records whose each connection
/// is; one this instance has no record of is dropped, since without a
/// backplane only the instance holding a connection can reach it anyway.
/// </summary>
public sealed class RealtimeAudience(IServiceScopeFactory scopes, ILogger<RealtimeAudience> logger) : IRealtimeAudience
{
    private readonly ConcurrentDictionary<string, (string UserId, string WorkspaceId)> _connections = new();

    public void Register(string connectionId, string userId, string workspaceId) =>
        _connections[connectionId] = (userId, workspaceId);

    public void Remove(string connectionId) => _connections.TryRemove(connectionId, out _);

    public async Task<IReadOnlyList<string>> FilterAsync(string workspaceId, IReadOnlyList<string> connections, Func<WorkspaceAccess, bool> allowed)
    {
        if (connections.Count == 0)
        {
            return connections;
        }

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        IWorkspaceAccessService access = scope.ServiceProvider.GetRequiredService<IWorkspaceAccessService>();

        Dictionary<string, bool> byUser = [];
        List<string> kept = [];
        foreach (string connection in connections)
        {
            if (!_connections.TryGetValue(connection, out (string UserId, string WorkspaceId) who) || who.WorkspaceId != workspaceId)
            {
                continue;
            }

            if (!byUser.TryGetValue(who.UserId, out bool may))
            {
                may = await access.GetAsync(who.UserId, workspaceId) is { } found && allowed(found);
                byUser[who.UserId] = may;
            }

            if (may)
            {
                kept.Add(connection);
            }
        }

        if (kept.Count < connections.Count)
        {
            logger.LogDebug("realtime.audience Kept={Kept} Of={Of}", kept.Count, connections.Count);
        }

        return kept;
    }
}
