using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Parrot.Application.Integrations;

/// <summary>
/// TikTok signs every webhook with the App Secret of the app that sends it:
/// <c>TikTok-Signature: t=&lt;unix seconds&gt;,s=&lt;hex HMAC-SHA256 of "{t}.{raw body}"&gt;</c>.
/// The timestamp is part of what is signed, so it cannot be changed without breaking the signature.
/// </summary>
public static class TikTokWebhookSignature
{
    /// <summary>
    /// Null when <paramref name="header"/> is the signature of <paramref name="rawBody"/> under
    /// <paramref name="appSecret"/> and is no older (or newer) than <paramref name="tolerance"/>;
    /// otherwise the reason it was refused, safe to log.
    /// </summary>
    public static string? Check(string rawBody, string? header, string appSecret, DateTimeOffset now, TimeSpan tolerance)
    {
        if (string.IsNullOrWhiteSpace(header)) return "missing_signature";

        string? timestamp = null;
        string? signature = null;
        foreach (string part in header.Split(','))
        {
            int equals = part.IndexOf('=');
            if (equals <= 0) continue;
            string key = part[..equals].Trim();
            string value = part[(equals + 1)..].Trim();
            if (key == "t") timestamp = value;
            else if (key == "s") signature = value;
        }

        if (timestamp is null || signature is null
            || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out long seconds)
            || signature.Length != 64 || !signature.All(char.IsAsciiHexDigit))
            return "malformed_signature";

        byte[] expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), Encoding.UTF8.GetBytes($"{timestamp}.{rawBody}"));
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(signature), expected)) return "mismatch";

        // After the signature, so only a timestamp TikTok really sent is judged.
        return Math.Abs(now.ToUnixTimeSeconds() - seconds) > tolerance.TotalSeconds ? "stale_timestamp" : null;
    }
}
