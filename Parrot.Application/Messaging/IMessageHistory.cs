namespace Parrot.Application.Messaging;

/// <summary>
/// A conversation's stored messages, read from MongoDB, the source of truth.
/// Used to recover anything realtime delivery missed: offline, disconnected,
/// or during a Redis or SignalR outage.
/// </summary>
public interface IMessageHistory
{
    /// <summary>
    /// The messages after <paramref name="afterSequence"/>, oldest first.
    /// Pass 0 to start from the beginning, then the page's
    /// <see cref="MessageHistoryPage.NextCursor"/> for the next page.
    /// </summary>
    /// <param name="ownerUserId">
    /// The signed-in CRM user. Only their messages are returned, so a
    /// conversation key, which is easy to guess, cannot open another
    /// business's history. Someone else's conversation reads as empty.
    /// </param>
    /// <param name="limit">How many to return at most. Implementations cap it.</param>
    Task<MessageHistoryPage> GetAfterAsync(
        string ownerUserId,
        string conversationKey,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default);
}

public sealed record MessageHistoryPage
{
    /// <summary>In <see cref="StoredMessage.Sequence"/> order.</summary>
    required public IReadOnlyList<StoredMessage> Messages { get; init; }

    /// <summary>The sequence to pass as afterSequence for the next page, or null when there is no more.</summary>
    public long? NextCursor { get; init; }
}
