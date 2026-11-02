using System.ComponentModel;
using Parrot.Catalog.Domain.Enums;

namespace Parrot.Catalog.Application.Contracts;

// ---- items: the dashboard's view of the catalog --------------------------
//
// One item is one product with its prices and availability, shaped like the
// "Add an item" form. The catalog, the product types and the SKUs behind it are
// chosen by the service, so the dashboard never asks the owner for them.

public enum ItemKind
{
    Product,
    Service,
}

public enum PricingMode
{
    Single,
    Variants,
    OnRequest,
}

public enum ItemFilter
{
    All,
    Available,
    Unavailable,
    Draft,
}

[Description("One way to buy the item, such as a size, with its price. Send id to keep an existing variant; leave it out to add one.")]
public sealed record ItemVariantInput(string Name, decimal Amount, Guid? Id = null);

[Description("single: amount, optionally per (\"person\", \"hour\"). variants: one price each. on_request: no amount; the business quotes it when asked.")]
public sealed record ItemPricingInput(
    PricingMode Mode,
    decimal? Amount = null,
    string? Per = null,
    IReadOnlyList<ItemVariantInput>? Variants = null,
    [property: Description("ISO 4217; the catalog's default currency when left out.")] string? Currency = null);

[Description("in_stock: optionally quantity. made_to_order: optionally lead_time_days. bookings: neither. available false takes it off offer without deleting it.")]
public sealed record ItemAvailabilityInput(AvailabilityMode Mode, bool Available = true, int? Quantity = null, int? LeadTimeDays = null);

[Description("What the Add an item form sends. status draft saves a draft; published makes it live, which needs the publish permission.")]
public sealed record SaveItemRequest(
    string Name,
    ItemKind Kind,
    ItemPricingInput Pricing,
    ItemAvailabilityInput Availability,
    Guid? CategoryId = null,
    string? Description = null,
    ProductStatus Status = ProductStatus.Draft);

public sealed record ItemVariant(Guid Id, string Name, decimal? Amount);

[Description("mode is null when no price has been set. min_amount and max_amount span the variants' prices.")]
public sealed record ItemPricing(PricingMode? Mode, string Currency, decimal? Amount, string? Per, decimal? MinAmount, decimal? MaxAmount, IReadOnlyList<ItemVariant> Variants);

public sealed record ItemAvailability(AvailabilityMode Mode, bool Available, int? Quantity, int? LeadTimeDays);

public sealed record ItemCategory(Guid Id, string Name);

[Description("availability is null when none has been recorded.")]
public sealed record ItemResponse(
    Guid Id,
    string Name,
    ItemKind Kind,
    ItemCategory? Category,
    string? Description,
    ProductStatus Status,
    ItemPricing Pricing,
    ItemAvailability? Availability,
    DateTime CreatedAt,
    DateTime UpdatedAt);

[Description("Counts for the list's tabs, after the category and search filters.")]
public sealed record ItemCounts(int All, int Available, int Unavailable, int Draft);

public sealed record ItemListResponse(IReadOnlyList<ItemResponse> Items, int Total, int Page, int PageSize, ItemCounts Counts);

public sealed record ItemListQuery(ItemFilter Filter, Guid? CategoryId, string? Query, int? Page, int? PageSize);

public sealed record CreateItemCategoryRequest(string Name);
