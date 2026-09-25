namespace Parrot.Web.Components.Pages.Dashboard.Catalog;

/// <summary>
/// What the editor hands back. Mutable, because a form is.
///
/// The editor keeps its own copy so a half-finished edit cannot leak into the
/// table behind it; the page copies the fields onto the real
/// <see cref="CatalogItem"/> only on save. This mirrors Aurora's ProductDraft,
/// trimmed to the fields Parrot's catalog actually holds.
/// </summary>
public sealed class CatalogDraft
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public string Currency { get; set; } = "NGN";
    public decimal Price { get; set; }
    public bool IsAvailable { get; set; } = true;

    public static CatalogDraft From(CatalogItem item) => new()
    {
        Name = item.Name,
        Description = item.Description,
        Category = item.Category,
        Currency = item.Currency,
        Price = item.Price,
        IsAvailable = item.IsAvailable
    };

    public CatalogItem ToItem() => ApplyTo(new CatalogItem());

    public CatalogItem ApplyTo(CatalogItem item)
    {
        item.Name = Name.Trim();
        item.Description = Description.Trim();
        item.Category = Category;
        item.Currency = Currency;
        item.Price = Price;
        item.IsAvailable = IsAvailable;
        return item;
    }
}
