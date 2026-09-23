using Parrot.Application.DTOs.Integrations;
using Parrot.Domain.Enums;

namespace Parrot.Application.Integrations;

// Persists inbound Messenger events consumed from Kafka; selected by platform in PlatformMessageHandlerFactory.
public sealed class FacebookMessageHandler(IMessageRepository repository) : IPlatformMessageHandler
{
    public MessagingPlatform Platform => MessagingPlatform.Facebook;

    // Stores the message as-is; unlike WhatsApp no per-type remapping is applied.
    public Task HandleAsync(IncomingMessage message, CancellationToken cancellationToken = default) =>
        repository.InsertAsync(message, cancellationToken);
}
