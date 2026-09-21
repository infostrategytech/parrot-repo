using Parrot.Domain.Entities.Messages;

namespace Parrot.Application.Conversations;

public record ConversationSummary(Guid ConnectionId, string Sender, string Name, string Preview, DateTime UpdatedAt);
public record ConversationMessage(string Id, string Text, bool Outgoing, string Status, DateTime ReceivedAt);
public record SendReplyRequest(Guid RequestId, string Text);
public record MessagePage(IReadOnlyList<ConversationMessage> Messages, string? NextBefore);

public interface IConversationStore
{
    Task<IReadOnlyList<ConversationSummary>> ListAsync(string owner, int skip, int take, CancellationToken ct);
    Task<MessagePage> ReadAsync(string owner, Guid connection, string sender, string? before, CancellationToken ct);
    Task<DateTime?> LastIncomingAsync(string owner, Guid connection, string sender, CancellationToken ct);
    Task<MessageDocument?> FindRequestAsync(string owner, Guid request, CancellationToken ct);
    Task<bool> ReserveAsync(WhatsAppMessageDocument message, CancellationToken ct);
    Task CompleteAsync(string id, string status, string? providerId, CancellationToken ct);
}

public interface IWhatsAppSender
{
    Task<string> SendAsync(string phoneNumberId, string recipient, string text, CancellationToken ct);
}

public sealed class WhatsAppRejectedException : Exception
{
    public WhatsAppRejectedException() : base("WhatsApp rejected the message. Check the recipient, access token and messaging permissions.") { }
}
