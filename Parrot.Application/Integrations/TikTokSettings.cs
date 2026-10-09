namespace Parrot.Application.Integrations;

public class TikTokSettings
{
    public const string SectionName = "TikTok";

    // Public HTTPS address of this API. TikTok's webhook callback and the OAuth redirect URI are built on it.
    public string WebhookBaseUrl { get; set; } = string.Empty;

    // TikTok API for Business version.
    public string ApiVersion { get; set; } = "v1.3";

    // How far a webhook's signed timestamp may be from now. TikTok documents no window; a resend after an
    // outage may carry its original time, so this is wider than a clock-skew allowance alone needs.
    public int SignatureToleranceSeconds { get; set; } = 300;
}
