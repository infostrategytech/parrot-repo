using Parrot.Application.DTOs.Integrations;
using Parrot.Domain.Enums;

namespace Parrot.Application.Integrations;

public sealed class FacebookMessageHandler(IMessageRepository repository) : IPlatformMessageHandler
{
    public MessagingPlatform Platform => MessagingPlatform.Facebook;

    public Task HandleAsync(IncomingMessage message, CancellationToken cancellationToken = default) =>
        repository.InsertAsync(message, cancellationToken);
}
