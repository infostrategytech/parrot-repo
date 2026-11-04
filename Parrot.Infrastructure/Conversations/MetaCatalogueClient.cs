using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Integrations;

namespace Parrot.Infrastructure.Conversations;

public sealed class MetaCatalogueClient(HttpClient http, IOptions<WhatsAppSettings> options) : IMetaCatalogueClient
{
    private static readonly string ProductFields = string.Join(",", [
        "id", "retailer_id", "name", "description", "price", "currency",
        "image_url", "additional_image_urls", "availability", "review_status",
        "url", "brand", "condition", "sale_price", "inventory",
    ]);

    public async Task<string?> GetCatalogueIdAsync(string wabaId, string accessToken, CancellationToken ct)
    {
        string version = Uri.EscapeDataString(options.Value.ApiVersion);
        string url = $"https://graph.facebook.com/{version}/{Uri.EscapeDataString(wabaId)}/product_catalogs";
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using HttpResponseMessage response = await http.SendAsync(request, ct);

        if ((int)response.StatusCode >= 500)
            throw new HttpRequestException($"Meta catalogue API failed ({(int)response.StatusCode}).");
        if (!response.IsSuccessStatusCode)
            throw new MetaCatalogueApiException("Meta refused the catalogue lookup.", await ErrorCodeAsync(response, ct));

        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!json.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array
            || data.GetArrayLength() == 0)
            return null;

        return data[0].TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : null;
    }

    public async Task<MetaCatalogueProductsPage> GetApprovedProductsAsync(
        string catalogueId, string accessToken, string? cursor, CancellationToken ct)
    {
        string version = Uri.EscapeDataString(options.Value.ApiVersion);
        string url = $"https://graph.facebook.com/{version}/{Uri.EscapeDataString(catalogueId)}/products"
            + $"?fields={Uri.EscapeDataString(ProductFields)}"
            + "&return_only_approved_products=true"
            + "&bulk_pagination=true";
        if (cursor is not null) url += $"&after={Uri.EscapeDataString(cursor)}";

        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using HttpResponseMessage response = await http.SendAsync(request, ct);

        if ((int)response.StatusCode >= 500)
            throw new HttpRequestException($"Meta catalogue API failed ({(int)response.StatusCode}).");
        if (!response.IsSuccessStatusCode)
            throw new MetaCatalogueApiException("Meta refused the products request.", await ErrorCodeAsync(response, ct));

        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        List<MetaCatalogueProduct> products = [];
        if (json.RootElement.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in data.EnumerateArray())
                products.Add(ParseProduct(item));
        }

        string? nextCursor = ExtractNextCursor(json.RootElement);
        return new MetaCatalogueProductsPage(products, nextCursor);
    }

    // Only returns a cursor when paging.next exists, meaning there is another page to fetch.
    private static string? ExtractNextCursor(JsonElement root)
    {
        if (!root.TryGetProperty("paging", out JsonElement paging)) return null;
        bool hasNextPage = paging.TryGetProperty("next", out JsonElement next)
            && next.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(next.GetString());
        if (!hasNextPage) return null;
        return paging.TryGetProperty("cursors", out JsonElement cursors)
            && cursors.TryGetProperty("after", out JsonElement after)
            && after.ValueKind == JsonValueKind.String
            ? after.GetString()
            : null;
    }

    private static MetaCatalogueProduct ParseProduct(JsonElement item)
    {
        List<string> additionalImages = [];
        if (item.TryGetProperty("additional_image_urls", out JsonElement extra) && extra.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement img in extra.EnumerateArray())
            {
                if (img.ValueKind == JsonValueKind.String && img.GetString() is { Length: > 0 } src)
                    additionalImages.Add(src);
            }
        }

        int? inventory = null;
        if (item.TryGetProperty("inventory", out JsonElement inv) && inv.TryGetInt32(out int stock))
            inventory = stock;

        return new MetaCatalogueProduct
        {
            MetaProductId = Text(item, "id") ?? string.Empty,
            RetailerId = Text(item, "retailer_id") ?? string.Empty,
            Name = Text(item, "name") ?? string.Empty,
            Description = Text(item, "description"),
            RawPrice = Text(item, "price"),
            RawSalePrice = Text(item, "sale_price"),
            Currency = Text(item, "currency"),
            ImageUrl = Text(item, "image_url"),
            AdditionalImageUrls = additionalImages,
            Availability = Text(item, "availability"),
            ReviewStatus = Text(item, "review_status"),
            Url = Text(item, "url"),
            Brand = Text(item, "brand"),
            Condition = Text(item, "condition"),
            Inventory = inventory,
        };
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // Meta's {"error":{"code":80009,...}}: a fixed set of codes, safe as a metric label.
    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return json.RootElement.TryGetProperty("error", out JsonElement error)
                && error.TryGetProperty("code", out JsonElement code) && code.TryGetInt64(out long value)
                ? value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
