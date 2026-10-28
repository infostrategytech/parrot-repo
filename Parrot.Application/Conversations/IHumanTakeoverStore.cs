namespace Parrot.Application.Conversations;

/// <summary>
/// Tracks whether a human agent has taken over a conversation from the AI.
/// Scoped per workspace + conversation: one state per (owner, connection, sender).
/// </summary>
public interface IHumanTakeoverStore
{
    /// <summary>Marks a human as active on the conversation. Idempotent.</summary>
    Task SetAsync(string owner, Guid connection, string sender, CancellationToken ct);

    /// <summary>Clears the takeover flag, handing the conversation back to the AI.</summary>
    Task ClearAsync(string owner, Guid connection, string sender, CancellationToken ct);

    /// <summary>True when a human has taken over and has not yet handed back.</summary>
    Task<bool> IsActiveAsync(string owner, Guid connection, string sender, CancellationToken ct);
}
