namespace Parrot.Application.Integrations;

public class TikTokSettings
{
    public const string SectionName = "TikTok";

    // Public HTTPS address of this API. TikTok's webhook callback and the OAuth redirect URI are built on it.
    public string WebhookBaseUrl { get; set; } = string.Empty;

    // TikTok API for Business version.
    public string ApiVersion { get; set; } = "v1.3";
}
