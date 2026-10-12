namespace Parrot.Application.Integrations;

public class InstagramSettings
{
    public const string SectionName = "Instagram";

    // Public HTTPS base of this API; the callback URL is {WebhookBaseUrl}/api/webhooks/instagram?ref=…
    public string WebhookBaseUrl { get; set; } = string.Empty;

    // Parrot's own Instagram app secret, for connections made through Parrot's app later. A manual
    // connection is checked with the secret saved on it instead.
    public string AppSecret { get; set; } = string.Empty;

    /// <summary>How webhook signatures are treated. Enforce, as for Facebook: Instagram is new, so no live connection predates its secret.</summary>
    public WebhookSignatureMode SignatureMode { get; set; } = WebhookSignatureMode.Enforce;

    // Graph API version on graph.instagram.com.
    public string ApiVersion { get; set; } = "v25.0";
}
