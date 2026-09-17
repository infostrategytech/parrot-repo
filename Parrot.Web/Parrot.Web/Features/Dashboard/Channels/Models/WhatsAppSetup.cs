namespace Parrot.Web.Features.Dashboard.Channels.Models;

public sealed record WhatsAppConnectionInfo(
    Guid Id, string Name, string SetupStatus, string? PhoneNumberId,
    string? DisplayPhoneNumber, DateTime? WebhookVerifiedAt, DateTime? DisabledAt)
{
    public string? WhatsAppBusinessAccountId { get; init; }
    public DateTime? FirstMessageReceivedAt { get; init; }
}

public sealed record WhatsAppSetup(
    WhatsAppConnectionInfo Connection, string WebhookUrl, string VerifyToken);
