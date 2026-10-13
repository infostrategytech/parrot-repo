using Parrot.Application.DTOs.Integrations;

namespace Parrot.Application.Messaging;

/// <summary>
/// The customer's display name for a channel whose webhook carries only an id (Messenger's PSID,
/// Instagram's IGSID), looked up from the channel's own API. Best effort and never throws, other than
/// for cancellation: a message is stored without a name rather than held up, and the inbox shows the id.
/// </summary>
public interface IContactNameLookup
{
    Task<string?> FindAsync(IncomingMessage message, CancellationToken cancellationToken);
}
