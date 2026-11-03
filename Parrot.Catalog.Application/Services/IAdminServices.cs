using Parrot.Catalog.Application.Contracts;
using Parrot.Catalog.Domain.Enums;

namespace Parrot.Catalog.Application.Services;

public interface ICatalogAdminService
{
    Task<CatalogResponse> CreateAsync(CreateCatalogRequest request, CancellationToken ct);

    Task<CatalogResponse> UpdateAsync(Guid id, UpdateCatalogRequest request, CancellationToken ct);

    Task<CatalogResponse> GetAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<CatalogResponse>> ListAsync(CancellationToken ct);

    Task DeleteAsync(Guid id, CancellationToken ct);
}

public interface IProductTypeAdminService
{
    Task<ProductTypeResponse> CreateAsync(CreateProductTypeRequest request, CancellationToken ct);

    Task<ProductTypeResponse> UpdateAsync(Guid id, UpdateProductTypeRequest request, CancellationToken ct);

    Task<ProductTypeResponse> GetAsync(Guid id, int? version, CancellationToken ct);

    Task<IReadOnlyList<ProductTypeResponse>> ListAsync(CancellationToken ct);

    Task<AttributeDefinitionResponse> AddAttributeAsync(Guid typeId, AttributeDefinitionInput input, CancellationToken ct);

    Task<AttributeDefinitionResponse> UpdateAttributeAsync(Guid typeId, Guid definitionId, UpdateAttributeDefinitionRequest request, CancellationToken ct);

    Task<ProductTypeResponse> CreateVersionAsync(Guid typeId, CreateTypeVersionRequest request, CancellationToken ct);

    Task<TypeSchemaResponse> GetSchemaAsync(Guid typeId, int? version, CancellationToken ct);
}

public interface ICategoryAdminService
{
    Task<CategoryResponse> CreateAsync(CreateCategoryRequest request, CancellationToken ct);

    Task<CategoryResponse> UpdateAsync(Guid id, UpdateCategoryRequest request, CancellationToken ct);

    Task<CategoryResponse> GetAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<CategoryResponse>> ListAsync(Guid? catalogId, CancellationToken ct);

    Task DeleteAsync(Guid id, CancellationToken ct);
}

public interface IProductAdminService
{
    Task<ProductAdminResponse> CreateAsync(CreateProductRequest request, CancellationToken ct);

    Task<ProductAdminResponse> UpdateAsync(Guid id, UpdateProductRequest request, CancellationToken ct);

    Task<ProductAdminResponse> GetAsync(Guid id, CancellationToken ct);

    Task<PagedResponse<ProductAdminResponse>> ListAsync(ProductListQuery query, CancellationToken ct);

    Task DeleteAsync(Guid id, CancellationToken ct);

    Task<ProductAdminResponse> PublishAsync(Guid id, CancellationToken ct);

    Task<ProductAdminResponse> RetireAsync(Guid id, CancellationToken ct);

    Task<ProductAdminResponse> ReactivateAsync(Guid id, CancellationToken ct);

    Task<VariantResponse> CreateVariantAsync(Guid productId, CreateVariantRequest request, CancellationToken ct);

    Task<VariantResponse> UpdateVariantAsync(Guid productId, Guid variantId, UpdateVariantRequest request, CancellationToken ct);

    Task DeleteVariantAsync(Guid productId, Guid variantId, CancellationToken ct);
}

public interface IOfferAdminService
{
    Task<OfferAdminResponse> CreateAsync(CreateOfferRequest request, CancellationToken ct);

    Task<OfferAdminResponse> UpdateAsync(Guid id, UpdateOfferRequest request, CancellationToken ct);

    Task<OfferAdminResponse> GetAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<OfferAdminResponse>> ListForProductAsync(Guid productId, CancellationToken ct);

    Task DeleteAsync(Guid id, CancellationToken ct);

    Task<PriceResponse> AddPriceAsync(Guid offerId, CreatePriceRequest request, CancellationToken ct);

    Task<PriceResponse> UpdatePriceAsync(Guid offerId, Guid priceId, UpdatePriceRequest request, CancellationToken ct);

    Task DeletePriceAsync(Guid offerId, Guid priceId, CancellationToken ct);
}

public interface IAvailabilityAdminService
{
    Task<AvailabilityResponse> CreateAsync(CreateAvailabilityRequest request, CancellationToken ct);

    Task<AvailabilityResponse> UpdateAsync(Guid id, UpdateAvailabilityRequest request, CancellationToken ct);

    Task<IReadOnlyList<AvailabilityResponse>> ListForProductAsync(Guid productId, CancellationToken ct);

    Task DeleteAsync(Guid id, CancellationToken ct);
}

public interface IRelationshipAdminService
{
    Task<RelationshipResponse> CreateAsync(CreateRelationshipRequest request, CancellationToken ct);

    Task<IReadOnlyList<RelationshipResponse>> ListForProductAsync(Guid productId, RelationshipType? type, CancellationToken ct);

    Task DeleteAsync(Guid id, CancellationToken ct);
}

public interface IMediaAdminService
{
    Task<MediaResponse> CreateAsync(CreateMediaRequest request, CancellationToken ct);

    Task<MediaResponse> UpdateAsync(Guid id, UpdateMediaRequest request, CancellationToken ct);

    Task<IReadOnlyList<MediaResponse>> ListForProductAsync(Guid productId, CancellationToken ct);

    Task DeleteAsync(Guid id, CancellationToken ct);
}

public interface ITypeVersionMigrator
{
    Task<TypeVersionMigrationReport> MigrateAsync(Guid typeId, int fromVersion, int toVersion, bool dryRun, CancellationToken ct);
}

/// <summary>The dashboard's items: each call reads or writes one product with its prices and availability, in one transaction.</summary>
public interface IItemService
{
    Task<ItemListResponse> ListAsync(ItemListQuery query, CancellationToken ct);

    Task<ItemResponse> GetAsync(Guid id, CancellationToken ct);

    Task<ItemResponse> CreateAsync(SaveItemRequest request, CancellationToken ct);

    Task<ItemResponse> UpdateAsync(Guid id, SaveItemRequest request, CancellationToken ct);

    Task<ItemResponse> SetAvailabilityAsync(Guid id, ItemAvailabilityInput request, CancellationToken ct);

    Task DeleteAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<ItemCategory>> ListCategoriesAsync(CancellationToken ct);

    Task<ItemCategory> CreateCategoryAsync(CreateItemCategoryRequest request, CancellationToken ct);
}
