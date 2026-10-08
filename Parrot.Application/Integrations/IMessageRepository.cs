using Parrot.Application.DTOs.Integrations;

namespace Parrot.Application.Integrations;

public interface IMessageRepository
{
    Task InsertAsync(IncomingMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// The first customer message on one of the owner's connections at or after
    /// <paramref name="since"/>: the Channels test's "did it arrive?". By Parrot's own
    /// connection id and owner, so a caller can only ever ask about their own connection.
    /// </summary>
    Task<ReceivedMessageSummary?> FindFirstAsync(
        Guid connectionId,
        string ownerUserId,
        DateTime since,
        CancellationToken cancellationToken = default);
}
