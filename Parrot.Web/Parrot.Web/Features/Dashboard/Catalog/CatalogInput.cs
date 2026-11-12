using System.Globalization;
using System.Text.RegularExpressions;

namespace Parrot.Web.Features.Dashboard.Catalog;

/// <summary>Owner-entered amounts are validated, never reinterpreted by removing punctuation.</summary>
public static partial class CatalogInput
{
    [GeneratedRegex(@"^(?:[0-9]+|[0-9]{1,3}(?:,[0-9]{3})+)(?:\.[0-9]{1,2})?$")]
    private static partial Regex AmountFormat();

    public static decimal? Money(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        if (value.StartsWith('₦')) value = value[1..].Trim();
        return AmountFormat().IsMatch(value)
            && decimal.TryParse(value, NumberStyles.AllowThousands | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var amount) && amount > 0 ? amount : null;
    }

    public static string Amount(decimal? amount) => amount is { } value
        ? value.ToString(value == decimal.Truncate(value) ? "#,##0" : "#,##0.00", CultureInfo.InvariantCulture) : "";

    public static string Price(CatalogEntry item)
    {
        if (item.PriceType == PriceType.OnRequest) return "Price on request";
        var prices = item.HasOptions
            ? item.Variants.Select(v => v.Price ?? item.Price).Where(p => p.HasValue).Select(p => p!.Value).ToList()
            : item.Price is { } price ? [price] : new List<decimal>();
        if (prices.Count == 0) return "Add a price";
        var prefix = item.PriceType == PriceType.From || prices.Distinct().Count() > 1 ? "From " : "";
        return $"{prefix}{Parrot.SharedComponents.DesignSystem.CurrencyAmount.Format(prices.Min(), item.CurrencyCode)}{(item.PriceUnit.Length > 0 ? " " + item.PriceUnit : "")}";
    }

    public static IReadOnlyList<string> Missing(CatalogEntry item)
    {
        List<string> result = [];
        if (string.IsNullOrWhiteSpace(item.Name)) result.Add("Add a name");
        if (item.PriceType != PriceType.OnRequest && (item.HasOptions
                ? item.Variants.Any(v => (v.Price ?? item.Price) is not > 0) : item.Price is not > 0))
            result.Add("Add a price");
        return result;
    }

    public static bool SupportedPostLink(string? text) => Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && new[] { "instagram.com", "www.instagram.com", "facebook.com", "www.facebook.com", "m.facebook.com" }
            .Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
}
