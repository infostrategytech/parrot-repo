using Parrot.Domain.Enums;

namespace Parrot.Application.Conversations;

/// <summary>
/// How long after a customer's last message the business may still reply with free text. One place
/// for the rule, so what the send path enforces and what the inbox shows cannot disagree.
/// </summary>
public static class ReplyWindow
{
    // WhatsApp, Messenger and Instagram allow 24 hours; TikTok allows 48.
    public static TimeSpan For(MessagingPlatform platform) =>
        platform == MessagingPlatform.TikTok ? TimeSpan.FromHours(48) : TimeSpan.FromHours(24);

    /// <summary>True while a reply is allowed, given when the customer last wrote.</summary>
    public static bool IsOpen(MessagingPlatform platform, DateTime lastCustomerMessageAt, DateTime now) =>
        lastCustomerMessageAt >= now - For(platform);
}
