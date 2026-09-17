using Parrot.Application.DTOs.Integrations;

namespace Parrot.Application.Integrations;

public interface IConnectionMessageReceipt
{
    Task RecordAsync(IncomingMessage message, CancellationToken cancellationToken);
}
