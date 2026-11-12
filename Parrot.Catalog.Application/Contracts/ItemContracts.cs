using System.ComponentModel;
using System.Text.Json;
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
    Food,
    Digital,
}

public enum PricingMode
{
    Single,
    Variants,
    OnRequest,
}

[Description("Where the AI assistant is with the item's latest save. updating: saved, the AI is still learning it. "
    + "ready: the AI has it and can find it. needs_attention: something the business can fix, such as a rejected photo; see photos and text_reason. "
    + "failed: the AI couldn't finish on its side. not_listed: a draft, hidden or removed item, which the AI keeps out of search.")]
public enum ItemAiState
{
    Updating,
    Ready,
    NeedsAttention,
    Failed,
    NotListed,
}

public enum ItemFilter
{
    All,
    Available,
    Unavailable,
    Draft,
}

[Description("One way to buy the item, such as a size, with its price. Send id to keep an existing variant; leave it out to add one. available false: sold out for now. amount may be left out only on a draft.")]
public sealed record ItemVariantInput(string Name, decimal? Amount, Guid? Id = null, bool Available = true);

[Description("single: amount, optionally per (\"person\", \"hour\"). variants: one price each. on_request: no amount; the business quotes it when asked.")]
public sealed record ItemPricingInput(
    PricingMode Mode,
    decimal? Amount = null,
    string? Per = null,
    IReadOnlyList<ItemVariantInput>? Variants = null,
    [property: Description("ISO 4217; the catalog's default currency when left out.")] string? Currency = null);

[Description("in_stock: optionally quantity. made_to_order: optionally lead_time_days. bookings: neither. available false takes it off offer without deleting it.")]
public sealed record ItemAvailabilityInput(AvailabilityMode Mode, bool Available = true, int? Quantity = null, int? LeadTimeDays = null);

[Description("What the Add an item form sends. status draft saves a draft; published makes it live, which needs the publish permission. "
    + "A draft may leave out amounts and availability. photos: keys from POST items/photos, cover first; leave it out to keep the photos as they are. "
    + "form_draft: the form as left, kept for a draft only.")]
public sealed record SaveItemRequest(
    string Name,
    ItemKind Kind,
    ItemPricingInput Pricing,
    ItemAvailabilityInput? Availability,
    Guid? CategoryId = null,
    string? Description = null,
    ProductStatus Status = ProductStatus.Draft,
    [property: Description("A service's length in the owner's words, such as \"3 hours\".")] string? Duration = null,
    [property: Description("How long making it takes, in the owner's words, such as \"About 1 week\".")] string? LeadTime = null,
    IReadOnlyList<string>? Photos = null,
    JsonElement? FormDraft = null,
    [property: Description("Kept but not offered, unlike sold out for now. A hidden item's availability is off.")] bool Hidden = false);

[Description("available false: sold out for now.")]
public sealed record ItemVariant(Guid Id, string Name, decimal? Amount, bool Available = true);

[Description("key goes back in an item's photos; url is where it can be seen.")]
public sealed record ItemPhoto(string Key, string Url);

[Description("mode is null when no price has been set. min_amount and max_amount span the variants' prices.")]
public sealed record ItemPricing(PricingMode? Mode, string Currency, decimal? Amount, string? Per, decimal? MinAmount, decimal? MaxAmount, IReadOnlyList<ItemVariant> Variants);

public sealed record ItemAvailability(AvailabilityMode Mode, bool Available, int? Quantity, int? LeadTimeDays);

public sealed record ItemCategory(Guid Id, string Name);

[Description("What the AI made of one photo. status: embedded (the AI learned it), rejected (it doesn't show the item; reason says why), "
    + "or failed (it couldn't be checked, e.g. the download failed). caption is what the AI saw in it. "
    + "photo_key matches a key in the item's photos when it is one of the item's own (not a variant's).")]
public sealed record ItemAiPhoto(Guid MediaId, string? PhotoKey, string Status, string? Caption, string? ReasonCode, string? Reason);

[Description("The AI assistant's progress on this item. reported_at is null until the AI has reported on any save. "
    + "searchable, text_status and photos describe the latest report and are empty while updating.")]
public sealed record ItemAi(
    ItemAiState State,
    DateTime? SentAt,
    DateTime? ReportedAt,
    bool? Searchable,
    string? TextStatus,
    string? TextReason,
    IReadOnlyList<ItemAiPhoto> Photos);

[Description("availability is null when none has been recorded. ai is null for items saved before the AI tracked them.")]
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
    DateTime UpdatedAt,
    string? Duration = null,
    string? LeadTime = null,
    IReadOnlyList<ItemPhoto>? Photos = null,
    JsonElement? FormDraft = null,
    bool Hidden = false,
    ItemAi? Ai = null);

[Description("Counts for the list's tabs, after the category and search filters.")]
public sealed record ItemCounts(int All, int Available, int Unavailable, int Draft);

public sealed record ItemListResponse(IReadOnlyList<ItemResponse> Items, int Total, int Page, int PageSize, ItemCounts Counts);

public sealed record ItemListQuery(ItemFilter Filter, Guid? CategoryId, string? Query, int? Page, int? PageSize);

public sealed record CreateItemCategoryRequest(string Name);
