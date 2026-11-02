using System.ComponentModel;
using System.Text.Json;
using Parrot.Catalog.Domain.Enums;

namespace Parrot.Catalog.Application.Contracts;

// ---- catalogs -------------------------------------------------------------

public sealed record CreateCatalogRequest(string Key, string Name, string DefaultCurrency);

public sealed record UpdateCatalogRequest(string Name, string DefaultCurrency, CatalogStatus Status);

public sealed record CatalogResponse(Guid Id, string Key, string Name, string DefaultCurrency, CatalogStatus Status, DateTime CreatedAt, DateTime UpdatedAt);

// ---- product types and the registry --------------------------------------

[Description("One attribute definition. Keys match ^[a-z][a-z0-9_]{0,63}$ and are unique within a type version.")]
public sealed record AttributeDefinitionInput(
    string Key,
    string Label,
    AttributeDataType DataType,
    string? Unit = null,
    bool Required = false,
    IReadOnlyList<string>? EnumOptions = null,
    bool Filterable = false,
    bool Searchable = false,
    bool Embeddable = false,
    bool VariantAxis = false,
    string? DisplayGroup = null,
    int SortOrder = 0,
    JsonElement? Validation = null);

[Description("Non-breaking edits to an existing definition. data_type, key and variant_axis cannot change inside a version.")]
public sealed record UpdateAttributeDefinitionRequest(
    string Label,
    string? Unit,
    bool Required,
    IReadOnlyList<string>? EnumOptions,
    bool Filterable,
    bool Searchable,
    bool Embeddable,
    string? DisplayGroup,
    int SortOrder,
    JsonElement? Validation);

public sealed record AttributeDefinitionResponse(
    Guid Id,
    int TypeVersion,
    string Key,
    string Label,
    AttributeDataType DataType,
    string? Unit,
    bool Required,
    IReadOnlyList<string> EnumOptions,
    bool Filterable,
    bool Searchable,
    bool Embeddable,
    bool VariantAxis,
    string? DisplayGroup,
    int SortOrder,
    JsonElement? Validation);

public sealed record CreateProductTypeRequest(string Key, string Name, ProductTypeKind Kind, IReadOnlyList<AttributeDefinitionInput>? Attributes = null);

public sealed record UpdateProductTypeRequest(string Name, ProductTypeStatus Status);

public sealed record ProductTypeResponse(
    Guid Id,
    string Key,
    string Name,
    ProductTypeKind Kind,
    int Version,
    ProductTypeStatus Status,
    IReadOnlyList<AttributeDefinitionResponse> Attributes,
    DateTime CreatedAt,
    DateTime UpdatedAt);

[Description("Creates version N+1 of a type from version N: add new definitions, remove keys, or change existing ones (including data_type).")]
public sealed record CreateTypeVersionRequest(
    IReadOnlyList<AttributeDefinitionInput>? Add = null,
    IReadOnlyList<string>? Remove = null,
    IReadOnlyList<AttributeDefinitionInput>? Change = null);

[Description("JSON Schema (2020-12) for the product's attributes and for a variant's attributes, with x-ui hints per property.")]
public sealed record TypeSchemaResponse(Guid TypeId, string TypeKey, int Version, JsonElement ProductSchema, JsonElement VariantSchema);

// ---- categories -----------------------------------------------------------

public sealed record CreateCategoryRequest(Guid CatalogId, Guid? ParentId, string Key, string Name, string? Slug = null, string? Description = null, int SortOrder = 0);

public sealed record UpdateCategoryRequest(Guid? ParentId, string Name, string? Slug, string? Description, int SortOrder);

public sealed record CategoryResponse(Guid Id, Guid CatalogId, Guid? ParentId, string Key, string Name, string Slug, string? Description, int SortOrder, IReadOnlyList<string> Path, DateTime CreatedAt, DateTime UpdatedAt);

// ---- products and variants -----------------------------------------------

public sealed record CreateProductRequest(
    Guid CatalogId,
    Guid ProductTypeId,
    Guid? CategoryId,
    string Sku,
    string Name,
    JsonElement Attributes,
    string? Slug = null,
    string? Description = null);

[Description("Status may move between draft and review here; publishing and retiring have their own endpoints.")]
public sealed record UpdateProductRequest(
    Guid? CategoryId,
    string Name,
    JsonElement Attributes,
    string? Slug = null,
    string? Description = null,
    ProductStatus? Status = null);

public sealed record CreateVariantRequest(string Sku, string Name, JsonElement Attributes, VariantStatus Status = VariantStatus.Active, int SortOrder = 0);

public sealed record UpdateVariantRequest(string Name, JsonElement Attributes, VariantStatus Status, int SortOrder);

public sealed record VariantResponse(Guid Id, Guid ProductId, string Sku, string Name, JsonElement Attributes, VariantStatus Status, int SortOrder, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record ProductAdminResponse(
    Guid Id,
    Guid CatalogId,
    TypeRef Type,
    Guid? CategoryId,
    string Sku,
    string Name,
    string Slug,
    string? Description,
    ProductStatus Status,
    JsonElement Attributes,
    int Version,
    DateTime? PublishedAt,
    DateTime? RetiredAt,
    string CreatedBy,
    string UpdatedBy,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<VariantResponse> Variants);

public sealed record ProductListQuery(Guid? CatalogId, ProductStatus? Status, Guid? ProductTypeId, Guid? CategoryId, string? Cursor, int? Limit);

// ---- offers and prices ----------------------------------------------------

public sealed record CreateOfferRequest(
    Guid? ProductId,
    Guid? VariantId,
    string Name,
    BillingPeriod BillingPeriod,
    DateTime ValidFrom,
    DateTime? ValidUntil = null,
    string? Description = null,
    OfferStatus Status = OfferStatus.Draft,
    JsonElement? Eligibility = null,
    int SortOrder = 0,
    [property: Description("What one price buys, such as person or hour.")] string? Unit = null,
    [property: Description("The business quotes the price when asked; the offer then carries no prices.")] bool PriceOnRequest = false);

public sealed record UpdateOfferRequest(
    string Name,
    BillingPeriod BillingPeriod,
    DateTime ValidFrom,
    DateTime? ValidUntil,
    string? Description,
    OfferStatus Status,
    JsonElement? Eligibility,
    int SortOrder,
    string? Unit = null,
    bool PriceOnRequest = false);

public sealed record CreatePriceRequest(decimal Amount, string Currency, PriceType PriceType, DateTime ValidFrom, DateTime? ValidUntil = null, PriceStatus Status = PriceStatus.Active);

public sealed record UpdatePriceRequest(decimal Amount, string Currency, PriceType PriceType, DateTime ValidFrom, DateTime? ValidUntil, PriceStatus Status);

public sealed record PriceResponse(Guid Id, Guid OfferId, decimal Amount, string Currency, PriceType PriceType, PriceStatus Status, DateTime ValidFrom, DateTime? ValidUntil, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record OfferAdminResponse(
    Guid Id,
    Guid CatalogId,
    Guid? ProductId,
    Guid? VariantId,
    string Name,
    string? Description,
    BillingPeriod BillingPeriod,
    OfferStatus Status,
    JsonElement Eligibility,
    DateTime ValidFrom,
    DateTime? ValidUntil,
    int SortOrder,
    string? Unit,
    bool PriceOnRequest,
    IReadOnlyList<PriceResponse> Prices,
    DateTime CreatedAt,
    DateTime UpdatedAt);

// ---- availability ---------------------------------------------------------

public sealed record CreateAvailabilityRequest(
    Guid? ProductId,
    Guid? VariantId,
    string Region,
    DateTime ValidFrom,
    DateTime? ValidUntil = null,
    int? StockQty = null,
    int? Capacity = null,
    AvailabilityStatus Status = AvailabilityStatus.Active,
    AvailabilityMode Mode = AvailabilityMode.InStock,
    [property: Description("Days needed to make it; only with mode made_to_order.")] int? LeadTimeDays = null);

public sealed record UpdateAvailabilityRequest(
    string Region,
    DateTime ValidFrom,
    DateTime? ValidUntil,
    int? StockQty,
    int? Capacity,
    AvailabilityStatus Status,
    AvailabilityMode Mode = AvailabilityMode.InStock,
    int? LeadTimeDays = null);

public sealed record AvailabilityResponse(Guid Id, Guid? ProductId, Guid? VariantId, string Region, int? StockQty, int? Capacity, AvailabilityMode Mode, int? LeadTimeDays, AvailabilityStatus Status, DateTime ValidFrom, DateTime? ValidUntil, DateTime CreatedAt, DateTime UpdatedAt);

// ---- relationships --------------------------------------------------------

public sealed record CreateRelationshipRequest(Guid SourceId, Guid TargetId, RelationshipType RelationshipType, JsonElement? Metadata = null, int SortOrder = 0);

public sealed record RelationshipResponse(Guid Id, Guid SourceId, Guid TargetId, RelationshipType RelationshipType, JsonElement Metadata, int SortOrder, DateTime CreatedAt);

// ---- media ----------------------------------------------------------------

[Description("Metadata only. The object itself is uploaded to S3 under the tenant's prefix; the key is recorded here.")]
public sealed record CreateMediaRequest(Guid ProductId, Guid? VariantId, MediaKind Kind, string S3Key, string MimeType, string? AltText = null, string? Title = null, long? SizeBytes = null, int SortOrder = 0);

public sealed record UpdateMediaRequest(string? AltText, string? Title, int SortOrder);

public sealed record MediaResponse(Guid Id, Guid ProductId, Guid? VariantId, MediaKind Kind, string S3Key, string MimeType, string? AltText, string? Title, long? SizeBytes, int SortOrder, DateTime CreatedAt);

// ---- type version migration ----------------------------------------------

public sealed record TypeVersionMigrationItem(Guid ProductId, string Sku, bool Valid, IReadOnlyList<string> Errors, IReadOnlyList<string> DroppedKeys);

public sealed record TypeVersionMigrationReport(Guid TypeId, int FromVersion, int ToVersion, bool DryRun, int Total, int Migrated, int Failed, IReadOnlyList<TypeVersionMigrationItem> Items);
