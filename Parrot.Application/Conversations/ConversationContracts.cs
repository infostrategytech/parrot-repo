using Parrot.Domain.Entities.Messages;

namespace Parrot.Application.Conversations;

public record ConversationSummary(Guid ConnectionId, string Sender, string Name, string Preview, DateTime UpdatedAt)
{
    /// <summary>whatsapp or facebook: which channel the conversation is on.</summary>
    public string Channel { get; init; } = "whatsapp";
}
public record ConversationMessage(string Id, string Text, bool Outgoing, string Status, DateTime ReceivedAt);
public record SendReplyRequest(Guid RequestId, string Text);
public record MessagePage(IReadOnlyList<ConversationMessage> Messages, string? NextBefore);

public interface IConversationStore
{
    Task<IReadOnlyList<ConversationSummary>> ListAsync(string owner, int skip, int take, CancellationToken ct);
    Task<MessagePage> ReadAsync(string owner, Guid connection, string sender, string? before, CancellationToken ct);

    // The customer's most recent message on this connection, or null when there is none.
    Task<MessageDocument?> LatestIncomingAsync(string owner, Guid connection, string sender, CancellationToken ct);
    Task<MessageDocument?> FindRequestAsync(string owner, Guid request, CancellationToken ct);
    Task<bool> ReserveAsync(MessageDocument message, CancellationToken ct);
    Task CompleteAsync(string id, string status, string? providerId, CancellationToken ct);
}

// When a reply was sent, found by the wamid Meta gave it: for delivery latency
// when Meta later reports the reply delivered or read. Null when unknown.
public interface IReplySendTimes
{
    Task<DateTime?> SentAtAsync(string providerMessageId, CancellationToken ct);
}

public interface IWhatsAppSender
{
    Task<string> SendAsync(string phoneNumberId, string accessToken, string recipient, string text, CancellationToken ct);
}

// Messenger's Send API, with the Page access token saved in setup. Returns Meta's message id (m_…).
public interface IFacebookSender
{
    Task<string> SendAsync(string pageId, string pageAccessToken, string recipient, string text, CancellationToken ct);
}

public sealed class FacebookRejectedException : Exception
{
    public FacebookRejectedException(string? metaErrorCode = null)
        : base("Messenger rejected the message. Check the Page access token and that the customer wrote within 24 hours.") =>
        MetaErrorCode = metaErrorCode;

    // Meta's error.code (e.g. "10" with subcode 2018278 outside the 24-hour window, "551" when the person is unavailable).
    public string? MetaErrorCode { get; }
}

public sealed class WhatsAppRejectedException : Exception
{
    public WhatsAppRejectedException(string? metaErrorCode = null)
        : base("WhatsApp rejected the message. Check the recipient, access token and messaging permissions.") =>
        MetaErrorCode = metaErrorCode;

    // Meta's error.code (e.g. "131047", "130429"), when the response carried one.
    public string? MetaErrorCode { get; }
}
