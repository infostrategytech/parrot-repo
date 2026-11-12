using System.Globalization;
using Parrot.SharedComponents.DesignSystem;
using System.Text.RegularExpressions;

namespace Parrot.Web.Features.Dashboard.Catalog.ConceptE;

/// <summary>
/// How Concept E says an item's price and choices (research, 25 October 2026: how Nigerian Instagram and
/// TikTok vendors price). The amount always comes first, in one
/// format; a quiet second line says what it depends on, only when it adds something. "From" means a starting
/// price and nothing else; choices that don't change the price (colour, flavour) never make a range.
/// </summary>
public static partial class PriceWords
{
    /// <summary>The price as the list shows it: the amount, and what it depends on.</summary>
    public sealed record Lines(string Main, string? Detail, bool Missing, bool Ask);

    /// <summary>
    /// The one option a list of variants is kept under (designer, 27 October 2026): each value is a variant as
    /// the owner writes it ("UK 6 blue", "128GB UK used"), with its own price.
    /// </summary>
    public const string VariantList = "Variant";

    public static bool IsVariantList(ItemOption option) => option.Name == VariantList;

    public static Lines For(CatalogEntry item)
    {
        var variants = item.HasOptions ? Count(item.Variants.Count) : null;
        if (item.PriceType == PriceType.OnRequest) return new("Price on request", variants ?? "Handed to you in chat", false, true);
        var prices = Prices(item);
        if (prices.Count == 0) return new("Add a price", variants, true, false);

        var low = prices.Min();
        var high = prices.Max();
        // Variants show their own prices, so a starting price says nothing more: their range or their one price.
        var main = item.PriceType == PriceType.From && !item.HasOptions ? $"From {CurrencyAmount.Format(low, item.CurrencyCode)}"
            : low == high ? CurrencyAmount.Format(low, item.CurrencyCode)
            : $"{CurrencyAmount.Format(low, item.CurrencyCode)} – {CurrencyAmount.Format(high, item.CurrencyCode)}";

        var parts = new List<string>();
        if (item.PriceUnit.Trim().Length > 0) parts.Add(item.PriceUnit.Trim());
        // The same count for old Size/Style options and new free-form variants. Never count the cartesian
        // product of options: only the variants actually saved for this item exist.
        if (variants is not null) parts.Add(variants);
        return new(main, parts.Count > 0 ? string.Join(" · ", parts) : null, false, false);
    }

    private static string Count(int variants) => variants == 1 ? "1 variant" : $"{variants} variants";

    /// <summary>One sentence of what the agent says about the price, or null when there's none yet.</summary>
    public static string? Sentence(CatalogEntry item)
    {
        if (item.PriceType == PriceType.OnRequest) return null;
        var prices = Prices(item);
        if (prices.Count == 0) return null;
        var low = prices.Min();
        var high = prices.Max();
        var unit = item.PriceUnit.Trim().Length > 0 ? $" {item.PriceUnit.Trim()}" : string.Empty;
        if (item.PriceType == PriceType.From && !item.HasOptions) return $"It starts from {CurrencyAmount.Format(low, item.CurrencyCode)}{unit}.";
        if (low == high)
            return item.HasOptions && Sizing(item.Options[0]) ? $"It's {CurrencyAmount.Format(low, item.CurrencyCode)}{unit}, the same for every {Singular(item.Options[0]).ToLowerInvariant()}." : $"It's {CurrencyAmount.Format(low, item.CurrencyCode)}{unit}.";
        return PriceOption(item) is { } option && !IsVariantList(option)
            ? $"It's {CurrencyAmount.Format(low, item.CurrencyCode)} to {CurrencyAmount.Format(high, item.CurrencyCode)}{unit}, by {Singular(option).ToLowerInvariant()}:"
            : $"It's {CurrencyAmount.Format(low, item.CurrencyCode)} to {CurrencyAmount.Format(high, item.CurrencyCode)}{unit}:";
    }

    /// <summary>The price of each value of the option that sets the price ("8 inches", ₦110,000), in order.</summary>
    public static IReadOnlyList<(string Value, decimal Price)> PriceList(CatalogEntry item)
    {
        if (!item.HasOptions) return [];
        if (PriceOption(item) is { } option)
        {
            var index = item.Options.IndexOf(option);
            return [.. option.Values
                .Select(value => (value, price: item.Variants.FirstOrDefault(variant => Part(variant.Key, index) == value) is { } variant ? variant.Price ?? item.Price : null))
                .Where(row => row.price is not null)
                .Select(row => (row.value, row.price!.Value))];
        }
        // Prices that differ by combination: each one.
        return [.. item.Variants.Where(variant => (variant.Price ?? item.Price) is not null).Select(variant => (variant.Key, (variant.Price ?? item.Price)!.Value))];
    }

    /// <summary>Only the assigned category belongs below the item name; uncategorised items have no second line.</summary>
    public static string Meta(CatalogEntry item) => item.Group.Trim();

    /// <summary>
    /// The option whose values set the price, when prices differ: grouping the combinations by its values
    /// gives one price each. Null when one price fits everything, or when no single option explains it.
    /// Among only some combinations (UK 6 blue, UK 12 red), a value on a single version proves nothing: every
    /// option would "explain" the price, so one does only when some value is shared by versions at one price.
    /// </summary>
    public static ItemOption? PriceOption(CatalogEntry item)
    {
        if (!item.HasOptions || Prices(item).Count < 2) return null;
        var some = item.Variants.Count < item.Options.Aggregate(1L, (total, option) => total * Math.Max(1, option.Values.Count));
        for (var index = 0; index < item.Options.Count; index++)
        {
            var at = index;
            var groups = item.Variants.GroupBy(variant => Part(variant.Key, at)).ToList();
            if (some && groups.All(group => group.Count() == 1)) continue;
            if (groups.All(group => group.Select(variant => variant.Price ?? item.Price).Distinct().Count() == 1)) return item.Options[at];
        }
        return null;
    }

    /// <summary>A choice's values, short: "UK 6–20", "8–14 inches", "S–XL", "Grey, Black", "5 colours".</summary>
    public static string Span(ItemOption option)
    {
        var values = option.Values.Where(value => value.Trim().Length > 0).Select(value => value.Trim()).ToList();
        if (values.Count == 0) return string.Empty;
        if (values.Count == 1) return values[0];
        if (Range(values) is { } range) return range;

        var letters = values.Select(value => Array.IndexOf(LetterSizes, value.ToUpperInvariant())).ToList();
        // S–XL for three sizes or more; two read better as they are ("S, M").
        if (values.Count >= 3 && letters.All(index => index >= 0)) return $"{LetterSizes[letters.Min()]}–{LetterSizes[letters.Max()]}";

        return values.Count <= 3 ? string.Join(", ", values) : $"{values.Count} {Plural(option).ToLowerInvariant()}";
    }

    /// <summary>Measures that make one range ("UK 6–20", "8–14 inches", "10–100ml"); null when they don't.</summary>
    private static string? Range(IEnumerable<string> all)
    {
        var values = all.Where(value => value.Trim().Length > 0).Select(value => value.Trim()).ToList();
        if (values.Count < 2) return null;
        var measures = values.Select(value => Measure().Match(value)).ToList();
        if (!measures.All(match => match.Success)) return null;
        var prefixes = measures.Select(match => match.Groups["pre"].Value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var units = measures.Select(match => (match.Groups["unit"].Value + match.Groups["unit2"].Value).Trim()).Where(unit => unit.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (prefixes.Count != 1 || units.Count > 1) return null;
        var numbers = measures.SelectMany(match => new[] { match.Groups["a"].Value, match.Groups["b"].Value })
            .Where(text => text.Length > 0)
            .Select(text => decimal.Parse(text, CultureInfo.InvariantCulture))
            .ToList();
        var unit = units.FirstOrDefault() ?? string.Empty;
        var gap = unit.Length > 2 ? " " : string.Empty;
        var pre = prefixes[0].Length > 0 ? prefixes[0] + " " : string.Empty;
        return $"{pre}{Number(numbers.Min())}–{Number(numbers.Max())}{(unit.Length > 0 ? gap + unit : string.Empty)}";
    }

    /// <summary>A choice of how much or how big (size, length, storage), where "the same for every one" is worth saying; not colour or flavour.</summary>
    public static bool Sizing(ItemOption option) =>
        !Regex.IsMatch(option.Name, @"colou?r|flavou?r|design|pattern|shade|style|print", RegexOptions.IgnoreCase);

    public static string Singular(ItemOption option) => option.Name.Trim().Length > 0 ? option.Name.Trim() : "Option";

    public static string Plural(ItemOption option)
    {
        var name = Singular(option);
        if (name.EndsWith('s')) return name;
        if (name.EndsWith('y') && name.Length > 1 && !"aeiou".Contains(name[^2])) return name[..^1] + "ies";
        return name + "s";
    }

    /// <summary>Naira as the business writes it: whole naira without decimals, kobo only when there is some.</summary>
    public static string Naira(decimal amount) => CurrencyAmount.Format(amount, CurrencyAmount.LegacyCode);

    /// <summary>The effective prices of an item: one, or one per combination.</summary>
    public static List<decimal> Prices(CatalogEntry item) => item.HasOptions
        ? [.. item.Variants.Select(variant => variant.Price ?? item.Price).Where(price => price is > 0).Select(price => price!.Value).Distinct()]
        : item.Price is decimal single && single > 0 ? [single] : [];

    /// <summary>One option's value inside a combination's key ("M / Indigo").</summary>
    public static string Part(string key, int index)
    {
        var parts = key.Split(" / ");
        return index < parts.Length ? parts[index] : key;
    }

    private static string Number(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static readonly string[] LetterSizes = ["XXS", "XS", "S", "M", "L", "XL", "XXL", "2XL", "3XL", "4XL"];

    // "UK 6–12", "8 inches", "10ml", "128GB", "42": a prefix, a number or a range of two, and a unit.
    [GeneratedRegex(@"^(?<pre>[A-Za-z]{1,4}\s+)?(?<a>\d+(?:\.\d+)?)\s*(?<unit>[A-Za-z""”]*)\s*(?:[–\-]\s*(?<b>\d+(?:\.\d+)?)\s*(?<unit2>[A-Za-z""”]*))?$")]
    private static partial Regex Measure();
}
