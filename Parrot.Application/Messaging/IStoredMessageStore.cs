using Parrot.Application.DTOs.Integrations;
using Parrot.Domain.Entities.Messages;

namespace Parrot.Application.Messaging;

/// <summary>Where the worker writes messages: one document per <c>MessageId</c>, whatever happens.</summary>
public interface IStoredMessageStore
{
    Task<MessageDocument?> FindAsync(string messageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts <paramref name="document"/>, or, if its <c>MessageId</c> is
    /// already stored, returns the stored one untouched. Either way the result
    /// is what is in the database.
    /// </summary>
    Task<MessageDocument> InsertOrGetAsync(MessageDocument document, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the outcome of copying one file: its status, reason, key, type and size, and drops the platform's
    /// link. Applies only while the file is still pending, so a repeat or a late duplicate changes nothing.
    /// Returns false when it did not apply.
    /// </summary>
    Task<bool> CompleteMediaAsync(string messageId, int index, IncomingMedia outcome, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes a reply that is pending for sending, setting it to "sending" and stamping when. Only one caller can:
    /// false means it was not pending (already taken or finished), and the caller must not send it.
    /// </summary>
    Task<bool> ClaimSendAsync(string messageId, DateTime claimedAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Messages stored before <paramref name="storedBefore"/> with at least one file still pending, oldest first, at most
    /// <paramref name="limit"/>. For the sweeper that finds media jobs that were lost or are stuck.
    /// </summary>
    Task<IReadOnlyList<StaleMessage>> FindStalePendingMediaAsync(DateTime storedBefore, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replies with a file stored before <paramref name="storedBefore"/> that are still "pending", and replies "sending"
    /// since before <paramref name="claimedBefore"/> (a worker that died mid-send), oldest first, at most <paramref name="limit"/>.
    /// </summary>
    Task<IReadOnlyList<StaleMessage>> FindStalePendingSendsAsync(DateTime storedBefore, DateTime claimedBefore, int limit, CancellationToken cancellationToken = default);

    /// <summary>Marks a reply failed only while it is still "pending"; false when a worker has taken it (its outcome is then the worker's).</summary>
    Task<bool> FailPendingSendAsync(string messageId, CancellationToken cancellationToken = default);

    /// <summary>Records how a claimed send ended (accepted, failed or unknown). Applies only while it is "sending"; false when it did not.</summary>
    Task<bool> CompleteSendAsync(string messageId, string status, string? providerId, CancellationToken cancellationToken = default);

    /// <summary>Our reply on this connection that the channel knows by <paramref name="providerMessageId"/>, or null.</summary>
    Task<MessageDocument?> FindByProviderIdAsync(Guid connectionId, string providerMessageId, CancellationToken cancellationToken = default) =>
        Task.FromResult<MessageDocument?>(null);
}

/// <summary>Announces a stored message on the persisted topic, keyed by conversation.</summary>
public interface IMessagePersistedPublisher
{
    Task PublishAsync(MessagePersisted persisted, CancellationToken cancellationToken = default);
}

/// <summary>A stored message the sweeper found waiting too long, with what it needs to queue the work again.</summary>
/// <param name="PendingMedia">The positions of the files still pending (empty for a reply).</param>
public sealed record StaleMessage(
    string MessageId, string? OwnerUserId, Guid? ConnectionId, string Recipient, DateTime StoredAt, string SendStatus, IReadOnlyList<int> PendingMedia);
