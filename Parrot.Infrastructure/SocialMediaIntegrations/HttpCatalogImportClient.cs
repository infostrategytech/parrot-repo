using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Application.Auth;
using Parrot.Application.Contracts.WhatsAppCatalogue;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Integrations;

namespace Parrot.Infrastructure.SocialMediaIntegrations;

/// <summary>
/// Calls the Parrot.Catalog internal WhatsApp import endpoints using a
/// short-lived service token with <c>catalog:admin</c> scope.
/// </summary>
public sealed class HttpCatalogImportClient(
    HttpClient http,
    IServiceTokenFactory tokenFactory) : ICatalogImportClient
{
    private const int MaxPageSize = 100;

    // Parrot.Catalog answers in snake_case and rejects any field it does not
    // know, so the client has to speak the same format on the way out and in.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public async Task<CatalogImportSyncResult> SyncProductsAsync(
        string workspaceId,
        Guid connectionId,
        IReadOnlyList<MetaCatalogueProduct> products,
        CancellationToken ct)
    {
        object body = new
        {
            ConnectionId = connectionId,
            Products = products.Select(p => new
            {
                RetailerId = p.RetailerId,
                Name = p.Name,
                Description = p.Description,
                Price = p.Price,
                SalePrice = p.SalePrice,
                Currency = p.Currency,
                ImageUrl = p.ImageUrl,
                Availability = p.Availability,
                Brand = p.Brand,
                Condition = p.Condition,
                Inventory = p.Inventory,
                MetaProductId = p.MetaProductId,
                MetaCatalogueId = (string?)null,
            }).ToList(),
        };

        using HttpRequestMessage request = BuildRequest(HttpMethod.Post, "api/catalog/internal/whatsapp/sync", workspaceId);
        request.Content = JsonContent.Create(body, options: JsonOptions);

        using HttpResponseMessage response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        CatalogSyncResponseDto result = await response.Content.ReadFromJsonAsync<CatalogSyncResponseDto>(JsonOptions, ct)
            ?? throw new HttpRequestException("Catalog sync returned an empty response.");

        return new CatalogImportSyncResult(result.Created, result.Updated, result.Failed);
    }

    public async Task<int> RetireMissingAsync(
        string workspaceId,
        Guid connectionId,
        IReadOnlyList<string> presentSkus,
        bool paginationCompleted,
        int validSkuCount,
        int failedProductCount,
        CancellationToken ct)
    {
        object body = new
        {
            ConnectionId = connectionId,
            PresentSkus = presentSkus,
            PaginationCompleted = paginationCompleted,
            ValidSkuCount = validSkuCount,
            FailedProductCount = failedProductCount,
        };

        using HttpRequestMessage request = BuildRequest(HttpMethod.Post, "api/catalog/internal/whatsapp/retire-missing", workspaceId);
        request.Content = JsonContent.Create(body, options: JsonOptions);

        using HttpResponseMessage response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        CatalogRetireResponseDto result = await response.Content.ReadFromJsonAsync<CatalogRetireResponseDto>(JsonOptions, ct)
            ?? throw new HttpRequestException("Catalog retire-missing returned an empty response.");

        return result.Retired;
    }

    public async Task<CatalogueItemsResponse> GetProductsAsync(
        string workspaceId,
        Guid connectionId,
        int page,
        int pageSize,
        string? cursor,
        CancellationToken ct)
    {
        int size = Math.Clamp(pageSize, 1, MaxPageSize);

        string url = $"api/catalog/internal/whatsapp/products?connectionId={connectionId}&limit={size}";
        if (cursor is not null)
        {
            url += $"&cursor={Uri.EscapeDataString(cursor)}";
        }

        using HttpRequestMessage request = BuildRequest(HttpMethod.Get, url, workspaceId);
        using HttpResponseMessage response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        CatalogProductsPageDto result = await response.Content.ReadFromJsonAsync<CatalogProductsPageDto>(JsonOptions, ct)
            ?? throw new HttpRequestException("Catalog products returned an empty response.");

        List<CatalogueItemResponse> items = result.Items
            .Select(p => new CatalogueItemResponse(
                p.Id,
                p.RetailerId,
                p.Name,
                p.Description,
                p.Price,
                p.Currency,
                p.ImageUrl,
                p.Availability,
                Url: null,
                p.Brand,
                p.Condition,
                p.SalePrice,
                Inventory: null,
                ChannelSource: "whatsapp",
                LastSyncedAt: p.UpdatedAt,
                CreatedAt: p.CreatedAt))
            .ToList();

        return new CatalogueItemsResponse(items, items.Count, page, size, result.NextCursor);
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string relativeUrl, string workspaceId)
    {
        HttpRequestMessage request = new (method, relativeUrl);
        string token = tokenFactory.MintForCatalog(workspaceId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    // ── Private response DTOs ────────────────────────────────────────────────
    private sealed record CatalogSyncResponseDto(
        Guid CatalogId, Guid ProductTypeId,
        int Created, int Updated, int Failed,
        IReadOnlyList<string> Errors);

    private sealed record CatalogRetireResponseDto(
        Guid CatalogId, int Retired, bool Skipped, string? SkipReason);

    private sealed record CatalogProductItemDto(
        Guid Id, string RetailerId, string Name, string? Description,
        string? ImageUrl, string? Brand, string? Availability, string? Condition,
        decimal? Price, string? Currency, decimal? SalePrice,
        string Status, DateTime? PublishedAt, DateTime? RetiredAt,
        DateTime CreatedAt, DateTime UpdatedAt);

    private sealed record CatalogProductsPageDto(
        IReadOnlyList<CatalogProductItemDto> Items, string? NextCursor);
}
