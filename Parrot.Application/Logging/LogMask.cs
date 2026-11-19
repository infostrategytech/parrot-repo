using System.Text.Json;
using System.Text.Json.Nodes;

namespace Parrot.Application.Logging;

/// <summary>
/// Contact details as logs show them: enough to recognise what was sent ("+234******4567",
/// "ad***@gmail.com"), never the whole value.
/// </summary>
public static class LogMask
{
    private const string Hidden = "***";

    // Logs are read by people: "+" and "&" as they are, not +.
    private static readonly JsonSerializerOptions Readable = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The leading "+" and country code, and the last four digits; short numbers keep two.</summary>
    public static string? Phone(string? phone)
    {
        if (string.IsNullOrEmpty(phone)) return phone;
        string digits = string.Concat(phone.Where(char.IsAsciiDigit));
        if (digits.Length == 0) return Hidden;

        int tail = digits.Length > 7 ? 4 : Math.Min(2, digits.Length);
        int head = digits.Length > 7 ? 3 : 0;
        string plus = phone.TrimStart().StartsWith('+') ? "+" : "";
        return plus + digits[..head] + new string('*', digits.Length - head - tail) + digits[^tail..];
    }

    /// <summary>The first two characters and the domain.</summary>
    public static string? Email(string? email)
    {
        if (string.IsNullOrEmpty(email)) return email;
        int at = email.IndexOf('@');
        if (at <= 0) return Text(email);
        return email[..Math.Min(2, at)] + Hidden + email[at..];
    }

    /// <summary>The first few characters.</summary>
    public static string? Text(string? text, int keep = 6)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= keep) return text;
        return text[..keep] + Hidden;
    }

    /// <summary>A channel profile kept as a JSON object: its email, address and phone fields masked, the rest as is.</summary>
    public static string? ProfileJson(string? json)
    {
        if (string.IsNullOrEmpty(json)) return json;
        JsonObject? profile;
        try { profile = JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { profile = null; }
        if (profile is null) return Text(json);

        foreach (KeyValuePair<string, JsonNode?> field in profile.ToList())
        {
            if (field.Value is not JsonValue value || !value.TryGetValue(out string? text)) continue;
            string? masked = field.Key switch
            {
                "email" => Email(text),
                "address" => Text(text),
                _ when field.Key.Contains("phone", StringComparison.OrdinalIgnoreCase) => Phone(text),
                _ => text,
            };
            if (masked != text) profile[field.Key] = masked;
        }

        return profile.ToJsonString(Readable);
    }
}
