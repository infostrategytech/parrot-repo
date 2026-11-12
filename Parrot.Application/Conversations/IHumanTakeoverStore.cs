namespace Parrot.Application.Conversations;

/// <summary>
/// Tracks whether a human agent has taken over a conversation from the AI.
/// Scoped per workspace + conversation: one state per (owner, connection, sender).
/// </summary>
public interface IHumanTakeoverStore
{
    /// <summary>
    /// Marks a human as active on the conversation. Idempotent.
    /// <paramref name="controlUntil"/> is when the takeover expires and the AI resumes; null means until manually handed back.
    /// </summary>
    Task SetAsync(string owner, Guid connection, string sender, DateTime? controlUntil, CancellationToken ct);

    /// <summary>Clears the takeover flag, handing the conversation back to the AI.</summary>
    Task ClearAsync(string owner, Guid connection, string sender, CancellationToken ct);

    /// <summary>
    /// Marks a human as active without changing the existing <c>ControlUntil</c> time limit.
    /// Used when a human sends a reply outside of an explicit timed takeover, so a timer
    /// set by <see cref="SetAsync"/> is preserved rather than overwritten with null.
    /// </summary>
    Task TouchAsync(string owner, Guid connection, string sender, CancellationToken ct);

    /// <summary>True when a human has taken over and the takeover has not expired or been handed back.</summary>
    Task<bool> IsActiveAsync(string owner, Guid connection, string sender, CancellationToken ct);

    /// <summary>
    /// Returns the active takeover expiry for each of the given conversations that has one.
    /// Conversations with no active takeover are absent. Null expiry means until manually handed back.
    /// </summary>
    Task<IReadOnlyDictionary<(Guid Connection, string Sender), DateTime?>> GetActiveAsync(
        string owner, IReadOnlyList<(Guid Connection, string Sender)> conversations, CancellationToken ct);
}
