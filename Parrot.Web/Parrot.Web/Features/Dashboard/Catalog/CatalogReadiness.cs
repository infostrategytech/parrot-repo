namespace Parrot.Web.Features.Dashboard.Catalog;

/// <summary>The basics required before an item can enter the catalogue, from any intake route.</summary>
public static class CatalogReadiness
{
    public static bool IsDraft(CatalogEntry item) => item.Status != CatalogStatus.Live || Issue(item) is not null;

    public static string? Issue(CatalogEntry item)
    {
        if (string.IsNullOrWhiteSpace(item.Name)) return "Give it a name.";
        if (item.Name.Trim().Length > 80) return "Keep the name to 80 characters or fewer.";
        if (!Enum.IsDefined(item.Kind)) return "Choose what kind of item this is.";
        if (item.AvailabilityIsUnconfirmed) return "Choose whether customers can have it now.";
        if (!Enum.IsDefined(item.PriceType)) return "Choose how this item is priced.";
        if (item.Options.Any(option => string.IsNullOrWhiteSpace(option.Name) || option.Values.Count == 0
            || option.Values.Any(string.IsNullOrWhiteSpace)
            || option.Values.Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != option.Values.Count)
            || (item.Options.Count > 0) != (item.Variants.Count > 0))
            return "Complete the variants, or remove the ones you don't need.";
        if (item.HasOptions && (item.Variants.Any(variant => string.IsNullOrWhiteSpace(variant.Key))
            || item.Variants.Select(variant => variant.Key.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != item.Variants.Count))
            return "Give each variant a different name.";
        if (item.TracksStock && (item.Stock is < 0 or > 999999 || item.Variants.Any(variant => variant.Stock is < 0 or > 999999)))
            return "Stock must be a whole number between 0 and 999,999.";
        if (item.PriceType == PriceType.OnRequest) return null;
        var prices = item.HasOptions ? item.Variants.Select(variant => variant.Price ?? item.Price) : [item.Price];
        if (prices.Any(price => price is { } amount && (amount <= 0 || decimal.Round(amount, 2) != amount)))
            return "Prices must be positive amounts with up to 2 decimal places.";
        if (item.HasOptions)
        {
            if (!item.Variants.Any(variant => (variant.Price ?? item.Price) is > 0)
                || item.Variants.Any(variant => variant.IsAvailable && (variant.Price ?? item.Price) is not > 0))
                return "Add a price for every available variant and at least one price overall.";
        }
        else if (item.Price is not > 0)
            return "Add a price, or choose Price on request.";
        return null;
    }
}
