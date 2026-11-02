using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Integrations;
using Parrot.Infrastructure.Conversations;

namespace Test.Parrot.Api.Tests.Services.Integrations;

public class MetaCatalogueClientTests
{
    // ── GetCatalogueIdAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetCatalogueId_ReturnsCatalogueIdFromFirstDataElement()
    {
        FakeHandler handler = new(HttpStatusCode.OK, """
            {"data":[{"id":"cat-999","name":"My Shop"}],"paging":{}}
            """);

        string? id = await Client(handler).GetCatalogueIdAsync("waba-123", "token-abc", default);

        Assert.Equal("cat-999", id);
        Assert.Equal("https://graph.facebook.com/v25.0/waba-123/product_catalogs", handler.Url);
        Assert.Equal("Bearer token-abc", handler.Authorization);
    }

    [Fact]
    public async Task GetCatalogueId_ReturnsNullWhenDataArrayIsEmpty()
    {
        FakeHandler handler = new(HttpStatusCode.OK, """{"data":[],"paging":{}}""");

        string? id = await Client(handler).GetCatalogueIdAsync("waba-123", "token-abc", default);

        Assert.Null(id);
    }

    [Fact]
    public async Task GetCatalogueId_ThrowsHttpRequestExceptionOnServerError()
    {
        FakeHandler handler = new(HttpStatusCode.InternalServerError, "{}");

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Client(handler).GetCatalogueIdAsync("waba-123", "token", default));
    }

    [Fact]
    public async Task GetCatalogueId_ThrowsMetaCatalogueApiExceptionOnClientError()
    {
        FakeHandler handler = new(HttpStatusCode.Unauthorized, """{"error":{"code":190,"message":"Invalid token"}}""");

        MetaCatalogueApiException ex = await Assert.ThrowsAsync<MetaCatalogueApiException>(() =>
            Client(handler).GetCatalogueIdAsync("waba-123", "bad-token", default));

        Assert.Equal("190", ex.ErrorCode);
    }

    [Fact]
    public async Task GetCatalogueId_ThrowsMetaCatalogueApiExceptionWithNullCodeWhenBodyHasNoErrorCode()
    {
        FakeHandler handler = new(HttpStatusCode.Forbidden, "<html>Forbidden</html>");

        MetaCatalogueApiException ex = await Assert.ThrowsAsync<MetaCatalogueApiException>(() =>
            Client(handler).GetCatalogueIdAsync("waba-123", "token", default));

        Assert.Null(ex.ErrorCode);
    }

    // ── GetApprovedProductsAsync ────────────────────────────────────────────────

    [Fact]
    public async Task GetApprovedProducts_RequestsCorrectQueryParameters()
    {
        FakeHandler handler = new(HttpStatusCode.OK, """{"data":[],"paging":{}}""");

        await Client(handler).GetApprovedProductsAsync("cat-1", "tok", null, default);

        Assert.NotNull(handler.Url);
        Assert.Contains("return_only_approved_products=true", handler.Url);
        Assert.Contains("bulk_pagination=true", handler.Url);
        Assert.Contains("fields=", handler.Url);
        Assert.Contains("retailer_id", Uri.UnescapeDataString(handler.Url!));
        Assert.Contains("image_url", Uri.UnescapeDataString(handler.Url!));
    }

    [Fact]
    public async Task GetApprovedProducts_AppendsCursorWhenProvided()
    {
        FakeHandler handler = new(HttpStatusCode.OK, """{"data":[],"paging":{}}""");

        await Client(handler).GetApprovedProductsAsync("cat-1", "tok", "cursor-xyz", default);

        Assert.Contains("after=cursor-xyz", handler.Url);
    }

    [Fact]
    public async Task GetApprovedProducts_ReturnsNullNextCursorOnLastPage()
    {
        FakeHandler handler = new(HttpStatusCode.OK, """
            {"data":[],"paging":{"cursors":{"before":"b","after":"a"}}}
            """);

        MetaCatalogueProductsPage page = await Client(handler).GetApprovedProductsAsync("cat-1", "tok", null, default);

        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task GetApprovedProducts_ReturnsNextCursorWhenPageHasMore()
    {
        FakeHandler handler = new(HttpStatusCode.OK, """
            {"data":[],"paging":{"cursors":{"before":"b","after":"next-cursor"},"next":"https://graph.facebook.com/..."}}
            """);

        MetaCatalogueProductsPage page = await Client(handler).GetApprovedProductsAsync("cat-1", "tok", null, default);

        Assert.Equal("next-cursor", page.NextCursor);
    }

    [Fact]
    public async Task GetApprovedProducts_ParsesProductFieldsCorrectly()
    {
        FakeHandler handler = new(HttpStatusCode.OK, """
            {
              "data": [{
                "id": "meta-prod-1",
                "retailer_id": "SKU-001",
                "name": "Blue T-Shirt",
                "description": "A cool shirt",
                "price": "2500 NGN",
                "currency": "NGN",
                "image_url": "https://example.com/img.jpg",
                "additional_image_urls": ["https://example.com/img2.jpg"],
                "availability": "in stock",
                "review_status": "approved",
                "url": "https://example.com/product",
                "brand": "BrandX",
                "condition": "new",
                "sale_price": "2000 NGN",
                "inventory": 42
              }],
              "paging": {}
            }
            """);

        MetaCatalogueProductsPage page = await Client(handler).GetApprovedProductsAsync("cat-1", "tok", null, default);

        Assert.Single(page.Products);
        MetaCatalogueProduct product = page.Products[0];
        Assert.Equal("meta-prod-1", product.MetaProductId);
        Assert.Equal("SKU-001", product.RetailerId);
        Assert.Equal("Blue T-Shirt", product.Name);
        Assert.Equal("A cool shirt", product.Description);
        Assert.Equal(25.00m, product.Price);
        Assert.Equal("NGN", product.Currency);
        Assert.Equal("https://example.com/img.jpg", product.ImageUrl);
        Assert.Equal(["https://example.com/img2.jpg"], product.AdditionalImageUrls);
        Assert.Equal("in stock", product.Availability);
        Assert.Equal("approved", product.ReviewStatus);
        Assert.Equal("https://example.com/product", product.Url);
        Assert.Equal("BrandX", product.Brand);
        Assert.Equal("new", product.Condition);
        Assert.Equal(20.00m, product.SalePrice);
        Assert.Equal(42, product.Inventory);
    }

    [Fact]
    public async Task GetApprovedProducts_ThrowsHttpRequestExceptionOnServerError()
    {
        FakeHandler handler = new(HttpStatusCode.BadGateway, "{}");

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Client(handler).GetApprovedProductsAsync("cat-1", "tok", null, default));
    }

    [Fact]
    public async Task GetApprovedProducts_ThrowsMetaCatalogueApiExceptionOnRateLimit()
    {
        FakeHandler handler = new(HttpStatusCode.TooManyRequests, """{"error":{"code":80009,"message":"Throttled"}}""");

        MetaCatalogueApiException ex = await Assert.ThrowsAsync<MetaCatalogueApiException>(() =>
            Client(handler).GetApprovedProductsAsync("cat-1", "tok", null, default));

        Assert.Equal("80009", ex.ErrorCode);
    }

    // ── Price parsing (via round-trip) ─────────────────────────────────────────

    [Theory]
    [InlineData("1000 USD", 10.00, "USD")]
    [InlineData("2500 NGN", 25.00, "NGN")]
    [InlineData("50 GBP", 0.50, "GBP")]
    [InlineData("0 EUR", 0.00, "EUR")]
    public async Task GetApprovedProducts_ParsesPriceMinorUnitsToMajor(string rawPrice, double expectedAmount, string expectedCurrency)
    {
        string body = "{\"data\":[{\"id\":\"p1\",\"retailer_id\":\"r1\",\"name\":\"Item\","
            + "\"price\":\"" + rawPrice + "\",\"currency\":\"" + expectedCurrency + "\"}],\"paging\":{}}";
        FakeHandler handler = new(HttpStatusCode.OK, body);

        MetaCatalogueProductsPage page = await Client(handler).GetApprovedProductsAsync("cat-1", "tok", null, default);

        Assert.Equal((decimal)expectedAmount, page.Products[0].Price);
        Assert.Equal(expectedCurrency, page.Products[0].Currency);
    }

    [Theory]
    [InlineData("nospace")]
    [InlineData("notanumber USD")]
    public async Task GetApprovedProducts_PriceIsNullForUnparsableValue(string rawPrice)
    {
        string body = "{\"data\":[{\"id\":\"p1\",\"retailer_id\":\"r1\",\"name\":\"Item\","
            + "\"price\":\"" + rawPrice + "\"}],\"paging\":{}}";
        FakeHandler handler = new(HttpStatusCode.OK, body);

        MetaCatalogueProductsPage page = await Client(handler).GetApprovedProductsAsync("cat-1", "tok", null, default);

        Assert.Null(page.Products[0].Price);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static MetaCatalogueClient Client(FakeHandler handler) =>
        new(new HttpClient(handler), Options.Create(new WhatsAppSettings { ApiVersion = "v25.0" }));

    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? Url { get; private set; }
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Url = request.RequestUri!.AbsoluteUri;
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
