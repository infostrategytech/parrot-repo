using System.Globalization;

namespace Parrot.Web.Components.Pages.Dashboard.Catalog;

/// <summary>
/// Money, formatted the way the product writes it.
///
/// Ported from Aurora's Naira: written out rather than delegated to a culture,
/// so the output is the same everywhere — ₦12,500, no decimals, because nobody
/// quotes kobo. Parrot's items carry a currency code, so anything that is not
/// naira keeps its code as the prefix, exactly as the old table printed it.
/// </summary>
public static class CatalogPrice
{
    public static string Symbol(string currency) => currency == "NGN" ? "₦" : currency;

    public static string Format(string currency, decimal amount) =>
        Symbol(currency) + Math.Round(amount, MidpointRounding.AwayFromZero)
            .ToString("#,##0", CultureInfo.InvariantCulture);
}
