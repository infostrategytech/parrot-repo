using Parrot.Catalog.Application.Contracts;
using Parrot.Catalog.Domain.Enums;

namespace Parrot.Catalog.Application.Services;

/// <summary>Reads over published data only, through the reader role and its views.</summary>
public interface ICatalogReadService
{
    Task<PagedResponse<ProductSummary>> ListProductsAsync(ReadListQuery query, CancellationToken ct);

    Task<ProductDetail> GetProductAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<OfferView>> GetOffersAsync(Guid productId, DateTime? asOf, CancellationToken ct);

    Task<IReadOnlyList<AvailabilityView>> GetAvailabilityAsync(Guid productId, string? region, DateTime? asOf, CancellationToken ct);

    Task<IReadOnlyList<RelatedProductView>> GetRelatedAsync(Guid productId, RelationshipType? type, CancellationToken ct);

    Task<DocumentView> GetDocumentAsync(Guid productId, CancellationToken ct);

    Task<SearchResponse> SearchAsync(SearchRequest request, int maxLimit, CancellationToken ct);

    Task<CompareResponse> CompareAsync(CompareRequest request, CancellationToken ct);
}

/// <summary>The narrow surface an LLM agent calls. Tenant comes from the token, never from arguments.</summary>
public interface IAiToolService
{
    Task<AiToolResponse<AiSearchResult>> SearchCatalogAsync(SearchCatalogToolRequest request, CancellationToken ct);

    Task<AiToolResponse<AiProduct>> GetProductAsync(GetProductToolRequest request, CancellationToken ct);

    Task<AiToolResponse<AiOffersResult>> GetProductOffersAsync(GetProductOffersToolRequest request, CancellationToken ct);

    Task<AiToolResponse<AiAvailabilityResult>> GetProductAvailabilityAsync(GetProductAvailabilityToolRequest request, CancellationToken ct);

    Task<AiToolResponse<AiCompareResult>> CompareProductsAsync(CompareProductsToolRequest request, CancellationToken ct);

    Task<AiToolResponse<AiRelatedResult>> GetRelatedProductsAsync(GetRelatedProductsToolRequest request, CancellationToken ct);
}
