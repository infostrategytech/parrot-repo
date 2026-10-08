using System.Security.Cryptography;
using System.Text;

namespace Parrot.Application.Integrations;

/// <summary>How a webhook whose X-Hub-Signature-256 does not check out is treated.</summary>
public enum WebhookSignatureMode
{
    /// <summary>Signatures are not checked.</summary>
    Off,

    /// <summary>A failure is logged, and the webhook is still accepted.</summary>
    LogOnly,

    /// <summary>A failure is logged, and the webhook is rejected with a 401.</summary>
    Enforce,
}

/// <summary>
/// Meta signs every webhook with the App Secret of the app that sends it:
/// <c>X-Hub-Signature-256: sha256=&lt;hex HMAC-SHA256 of the raw body&gt;</c>. A manually set up
/// connection uses the customer's own app, so its webhooks are checked with that connection's
/// secret; a connection made through Parrot's app (Embedded Signup) uses the app-level secret.
/// </summary>
public static class WebhookSignature
{
    /// <summary>A Meta App Secret: 32 hexadecimal characters.</summary>
    public static bool IsAppSecret(string? value) =>
        value is { Length: 32 } && value.All(char.IsAsciiHexDigit);

    /// <summary>True when <paramref name="signatureHeader"/> is the HMAC of <paramref name="rawBody"/> under <paramref name="appSecret"/>.</summary>
    public static bool Matches(string rawBody, string? signatureHeader, string appSecret)
    {
        if (string.IsNullOrEmpty(appSecret) || signatureHeader is null
            || !signatureHeader.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string receivedHex = signatureHeader["sha256=".Length..];
        if (receivedHex.Length != 64 || !receivedHex.All(char.IsAsciiHexDigit))
        {
            return false;
        }

        byte[] expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), Encoding.UTF8.GetBytes(rawBody));
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(receivedHex), expected);
    }
}
