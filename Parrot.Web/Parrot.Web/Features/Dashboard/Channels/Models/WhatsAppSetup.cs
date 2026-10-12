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

/// <summary>Instagram's manual setup as the API reports it; credentials only as "saved or not".</summary>
public sealed record InstagramWebhookSetup(
    Guid ConnectionId,
    string WebhookUrl,
    string VerifyToken,
    bool WebhookVerified,
    string? AccountId,
    string? Username,
    bool HasAccessToken,
    bool HasAppSecret)
{
    /// <summary>When the saved token runs out; Parrot renews it well before then.</summary>
    public DateTime? AccessTokenExpiresAt { get; init; }

    /// <summary>Instagram refused the saved token, or it ran out: replies stop until a new one is pasted.</summary>
    public bool NeedsNewToken { get; init; }
}

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

/// <summary>
/// Where the TikTok setup stands, as the API reports it: the app's own redirect URI to add in TikTok, whether
/// its App ID and secret are saved, and whether the owner has authorized the account.
/// </summary>
public sealed record TikTokSetup(
    Guid? ConnectionId,
    string RedirectUri,
    string? AppId,
    bool Authorized,
    string? DisplayName)
{
    /// <summary>Authorized once, but TikTok access has lapsed: replies cannot be sent until it is authorized again.</summary>
    public bool NeedsReauthorization { get; init; }
}

/// <summary>Where to send the browser so the owner can approve their TikTok app.</summary>
public sealed record TikTokAuthorization(string AuthorizationUrl);
