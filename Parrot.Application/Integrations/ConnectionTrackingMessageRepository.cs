using Parrot.Application.DTOs.Integrations;

namespace Parrot.Application.Integrations;

public sealed class ConnectionTrackingMessageRepository : IMessageRepository
{
    private readonly IMessageRepository _messages;
    private readonly IConnectionMessageReceipt _receipts;

    public ConnectionTrackingMessageRepository(IMessageRepository messages, IConnectionMessageReceipt receipts)
    {
        _messages = messages;
        _receipts = receipts;
    }

    public async Task InsertAsync(IncomingMessage message, CancellationToken cancellationToken = default)
    {
        await _messages.InsertAsync(message, cancellationToken);
        if (message.ConnectionId.HasValue)
        {
            // If PostgreSQL fails, Kafka retries the already-idempotent MongoDB write and this update.
            await _receipts.RecordAsync(message, cancellationToken);
        }
    }

    public Task<ReceivedMessageSummary?> FindFirstAsync(string channelId, DateTime since, CancellationToken cancellationToken = default) =>
        _messages.FindFirstAsync(channelId, since, cancellationToken);
}
