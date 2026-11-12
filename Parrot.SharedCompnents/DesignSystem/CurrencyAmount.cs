using System.Globalization;
using System.Text.RegularExpressions;

namespace Parrot.SharedComponents.DesignSystem;

/// <summary>Formatting only, never exchange-rate conversion. Always pass the record's currency for saved money.</summary>
public static class CurrencyAmount
{
    public const string LegacyCode = "NGN";
    public static string Code(string? code) => string.IsNullOrWhiteSpace(code) ? LegacyCode : code.Trim().ToUpperInvariant();
    public static string Symbol(string? code) => Code(code) switch
    {
        "NGN" => "₦", "GHS" => "GH₵", "USD" => "$", "GBP" => "£", "EUR" => "€", _ => Code(code),
    };
    /// <summary>Explicit source currency wins over a workspace default. No inference from location or exchange rates.</summary>
    public static string? ExplicitCode(string text)
    {
        var codes = ExplicitCodes(text);
        return codes.Count == 1 ? codes[0] : null;
    }
    public static IReadOnlyList<string> ExplicitCodes(string text)
    {
        var codes = new List<string>();
        if (Regex.IsMatch(text, @"₦|\bNGN\b|\bnaira\b|(?<![A-Za-z])N(?=\s?\d)", RegexOptions.IgnoreCase)) codes.Add("NGN");
        if (Regex.IsMatch(text, @"GH₵|\bGHS\b|\bcedis?\b", RegexOptions.IgnoreCase)) codes.Add("GHS");
        if (Regex.IsMatch(text, @"£|\bGBP\b", RegexOptions.IgnoreCase)) codes.Add("GBP");
        if (Regex.IsMatch(text, @"€|\bEUR\b", RegexOptions.IgnoreCase)) codes.Add("EUR");
        if (Regex.IsMatch(text, @"\$|\bUSD\b", RegexOptions.IgnoreCase)) codes.Add("USD");
        return codes;
    }
    public static string Format(decimal amount, string? code)
    {
        var symbol = Symbol(code);
        var number = amount == decimal.Truncate(amount) ? amount.ToString("#,##0", CultureInfo.InvariantCulture)
            : amount.ToString("#,##0.00", CultureInfo.InvariantCulture);
        return symbol + (symbol == Code(code) ? " " : "") + number;
    }
}
