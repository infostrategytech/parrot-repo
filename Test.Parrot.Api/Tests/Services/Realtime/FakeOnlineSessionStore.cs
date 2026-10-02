using Parrot.Application.Realtime;

namespace Test.Parrot.Api.Tests.Services.Realtime;

/// <summary>An in-memory session store; set <see cref="Failure"/> to make every call throw, as Redis down would.</summary>
public sealed class FakeOnlineSessionStore : IOnlineSessionStore
{
    private readonly Dictionary<string, HashSet<string>> _sessions = new();

    public Exception? Failure { get; set; }

    public int Registrations { get; private set; }

    public Task RegisterAsync(string userId, string connectionId, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        Registrations++;
        if (!_sessions.TryGetValue(userId, out HashSet<string>? connections))
            _sessions[userId] = connections = new HashSet<string>();
        connections.Add(connectionId);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string userId, string connectionId, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        if (_sessions.TryGetValue(userId, out HashSet<string>? connections)) connections.Remove(connectionId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> GetConnectionsAsync(string userId, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        IReadOnlyList<string> live = _sessions.TryGetValue(userId, out HashSet<string>? connections)
            ? connections.Order().ToArray()
            : [];
        return Task.FromResult(live);
    }

    /// <summary>Forgets every session, as a Redis restart does.</summary>
    public void Clear() => _sessions.Clear();

    private void ThrowIfFailing()
    {
        if (Failure is not null) throw Failure;
    }
}
