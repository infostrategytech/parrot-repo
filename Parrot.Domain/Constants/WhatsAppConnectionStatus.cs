namespace Parrot.Domain.Constants;

public static class WhatsAppConnectionStatus
{
    public const string Disabled = "disabled";
    public const string AwaitingWebhookVerification = "awaiting-webhook-verification";
    public const string AwaitingTestMessage = "awaiting-test-message";
    public const string ReceivingConfigured = "receiving-configured";
}
