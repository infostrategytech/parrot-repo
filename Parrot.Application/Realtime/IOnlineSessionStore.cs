namespace Parrot.Application.Realtime;

/// <summary>
/// Which SignalR connections each CRM user has open right now. One user can
/// have several (two tabs, a laptop and a phone), and a connection that stops
/// renewing drops out on its own once its TTL runs out.
///
/// This is routing information only. It never holds messages, history or
/// delivery state, so losing all of it loses nothing: a client that is still
/// connected registers again on its next heartbeat.
/// </summary>
public interface IOnlineSessionStore
{
    /// <summary>Marks the connection online for <paramref name="userId"/>, or renews it if it already is.</summary>
    /// <remarks>
    /// Register and renew are one operation on purpose: a heartbeat after
    /// Redis was emptied puts the session straight back.
    /// </remarks>
    Task RegisterAsync(string userId, string connectionId, CancellationToken cancellationToken = default);

    /// <summary>Removes the connection. Removing one that is not there does nothing.</summary>
    Task RemoveAsync(string userId, string connectionId, CancellationToken cancellationToken = default);

    /// <summary>The user's live connections; empty when they are offline.</summary>
    Task<IReadOnlyList<string>> GetConnectionsAsync(string userId, CancellationToken cancellationToken = default);
}
