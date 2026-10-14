using Parrot.Application.DTOs.Integrations;

namespace Parrot.Application.Messaging;

/// <summary>
/// Copies the files of an incoming message into private storage, because the platform's own links expire
/// (WhatsApp's within minutes, and only with the connection's token). Best effort and never throws, other
/// than for cancellation: a file that cannot be saved comes back without a StorageKey and the message is
/// stored anyway, so a slow or refusing platform never holds up the conversation.
/// </summary>
public interface IInboundMediaStore
{
    /// <summary>The message's media with ContentType, SizeBytes, FileName and StorageKey filled in for every file that was saved.</summary>
    Task<IReadOnlyList<IncomingMedia>> SaveAsync(IncomingMessage message, CancellationToken cancellationToken);
}
