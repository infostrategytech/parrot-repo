using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Parrot.Application.Realtime;
using Parrot.Api.Security;

namespace Parrot.Api.Realtime;

/// <summary>
/// The CRM's realtime connection. Connecting marks the session's workspace online
/// in Redis, <see cref="Heartbeat"/> keeps it online, and disconnecting takes
/// it off. Persisted messages are pushed to their connections from the
/// server; clients only listen, and fetch anything they missed from history.
///
/// Every Redis call is best-effort. Redis being down must not refuse or drop
/// the connection: the client simply misses live pushes until its next
/// heartbeat finds Redis back, and recovers the gap from history.
/// </summary>
[Authorize]
public sealed class MessagingHub(IOnlineSessionStore sessions, ILogger<MessagingHub> logger) : Hub
{
    public const string Path = "/hubs/messaging";

    /// <summary>The client method a persisted message is pushed to.</summary>
    public const string MessagePersistedMethod = "messagePersisted";

    /// <summary>The client method pushed when a channel reports receipts for replies.</summary>
    public const string ReceiptsChangedMethod = "receiptsChanged";

    /// <summary>The client method pushed when one of a customer's files has finished loading.</summary>
    public const string MediaChangedMethod = "mediaChanged";

    /// <summary>The client method an agent's activity (a turn, an approval asked for, an escalation) is pushed to.</summary>
    public const string AgentActivityMethod = "agentActivity";

    public override async Task OnConnectedAsync()
    {
        await RegisterAsync("session.registered");
        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Called by the client on an interval shorter than the session TTL. It
    /// re-registers rather than only renewing, so a client reconnected after
    /// a Redis restart is back online on its next beat.
    /// </summary>
    public Task Heartbeat() => RegisterAsync("session.renewed");

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        string owner = Owner();
        try
        {
            // Not ConnectionAborted: it has already fired for a closed connection.
            await sessions.RemoveAsync(owner, Context.ConnectionId, CancellationToken.None);
            logger.LogDebug("session.removed ConnectionId={ConnectionId}", Context.ConnectionId);
        }
        catch (Exception ex)
        {
            // The TTL removes it instead.
            logger.LogWarning(ex, "session.remove_failed ConnectionId={ConnectionId}; it will expire by TTL", Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    private async Task RegisterAsync(string step)
    {
        try
        {
            await sessions.RegisterAsync(Owner(), Context.ConnectionId, Context.ConnectionAborted);
            logger.LogDebug("{Step} ConnectionId={ConnectionId}", step, Context.ConnectionId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "session.register_failed Step={Step} ConnectionId={ConnectionId}; retried on the next heartbeat",
                step, Context.ConnectionId);
        }
    }

    // Pushes go to a workspace's open sessions, so a connection registers under
    // the workspace its token works in. [Authorize] guarantees a principal.
    private string Owner() =>
        Context.User?.WorkspaceId()
        ?? throw new HubException("Workspace claim not found.");
}
