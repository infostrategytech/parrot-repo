namespace Parrot.Application.Integrations;

public class TikTokSettings
{
    public const string SectionName = "TikTok";

    // Public HTTPS address of this API. TikTok's webhook callback and the OAuth redirect URI are built on it.
    public string WebhookBaseUrl { get; set; } = string.Empty;

    // TikTok API for Business version.
    public string ApiVersion { get; set; } = "v1.3";

    // Where TikTok's API is called and where its consent screen is. Only a stand-in for TikTok, in local
    // development, has reason to change them.
    public string ApiBaseUrl { get; set; } = "https://business-api.tiktok.com";
    public string AuthorizeUrl { get; set; } = "https://www.tiktok.com/v2/auth/authorize/";

    // App secrets, codes and tokens travel to these, so a misconfigured address must not send them in the
    // clear: HTTPS, or a stand-in on this machine.
    public static string RequireSecureUrl(string url, string setting) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            ? url
            : throw new InvalidOperationException($"TikTok:{setting} must be an HTTPS URL (or http on localhost).");

    // How far a webhook's signed timestamp may be from now. TikTok documents no window; a resend after an
    // outage may carry its original time, so this is wider than a clock-skew allowance alone needs.
    public int SignatureToleranceSeconds { get; set; } = 300;
}
