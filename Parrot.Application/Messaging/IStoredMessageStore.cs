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

    /// <summary>Our reply on this connection that the channel knows by <paramref name="providerMessageId"/>, or null.</summary>
    Task<MessageDocument?> FindByProviderIdAsync(Guid connectionId, string providerMessageId, CancellationToken cancellationToken = default) =>
        Task.FromResult<MessageDocument?>(null);
}

/// <summary>Announces a stored message on the persisted topic, keyed by conversation.</summary>
public interface IMessagePersistedPublisher
{
    Task PublishAsync(MessagePersisted persisted, CancellationToken cancellationToken = default);
}
