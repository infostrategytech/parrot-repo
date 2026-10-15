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
}

/// <summary>Announces a stored message on the persisted topic, keyed by conversation.</summary>
public interface IMessagePersistedPublisher
{
    Task PublishAsync(MessagePersisted persisted, CancellationToken cancellationToken = default);
}
