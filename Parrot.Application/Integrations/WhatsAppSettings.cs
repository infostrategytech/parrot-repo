namespace Parrot.Application.Integrations;

public class WhatsAppSettings
{
    public const string SectionName = "WhatsApp";

    public string WebhookBaseUrl { get; set; } = string.Empty;
    public string VerifyToken { get; set; } = string.Empty;
    public string AppSecret { get; set; } = string.Empty;

    /// <summary>
    /// How webhook signatures are treated. LogOnly until every manual connection has saved its
    /// App Secret: a failure is logged but the webhook is still accepted. Then Enforce.
    /// </summary>
    public WebhookSignatureMode SignatureMode { get; set; } = WebhookSignatureMode.LogOnly;
    // Legacy configuration retained for compatibility; sending uses the connection credential.
    public string AccessToken { get; set; } = string.Empty;
    public string PhoneNumberId { get; set; } = string.Empty;
    public string BusinessAccountId { get; set; } = string.Empty;
    public string ApiVersion { get; set; } = "v25.0";
}
