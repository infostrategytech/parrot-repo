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

    /// <summary>Records how a claimed send ended (accepted, failed or unknown). Applies only while it is "sending"; false when it did not.</summary>
    Task<bool> CompleteSendAsync(string messageId, string status, string? providerId, CancellationToken cancellationToken = default);
}

/// <summary>Announces a stored message on the persisted topic, keyed by conversation.</summary>
public interface IMessagePersistedPublisher
{
    Task PublishAsync(MessagePersisted persisted, CancellationToken cancellationToken = default);
}
