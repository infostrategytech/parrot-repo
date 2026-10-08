namespace Parrot.Web.Features.Dashboard.Channels.Models;

public sealed record WhatsAppConnectionInfo(
    Guid Id, string Name, string SetupStatus, string? PhoneNumberId,
    string? DisplayPhoneNumber, DateTime? WebhookVerifiedAt, DateTime? DisabledAt)
{
    public bool HasAccessToken { get; init; }
    /// <summary>The App Secret of the business's own Meta app is saved; webhooks are checked with it.</summary>
    public bool HasAppSecret { get; init; }
    public string? WhatsAppBusinessAccountId { get; init; }
    public DateTime? FirstMessageReceivedAt { get; init; }
}

public sealed record WhatsAppSetup(
    WhatsAppConnectionInfo Connection, string WebhookUrl, string VerifyToken);

public sealed record FacebookWebhookSetup(
    Guid ConnectionId,
    string WebhookUrl,
    string VerifyToken,
    bool WebhookVerified,
    string? PageId,
    string? PageName,
    bool HasAccessToken)
{
    /// <summary>The App Secret of the business's own Meta app is saved; webhooks are checked with it.</summary>
    public bool HasAppSecret { get; init; }
}
