using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Parrot.Web.Features.Checkout;
using Parrot.Web.Services;

namespace Test.Parrot.Api.Tests.Web.Checkout;

// Controlled contract responses only: a customer's checkout link, read and paid without a session.
public class CheckoutClientTests
{
    private static object View(string state = "open", decimal total = 24000m) => new
    {
        state, business = "Example business", logoUrl = (string?)null, customerName = "Example customer", currency = "NGN",
        lines = new[]
        {
            new { name = "Example item", variantName = (string?)"Large", quantity = 1, quotedUnitPrice = 15000m, unitPrice = (decimal?)16000m,
                  lineTotal = (decimal?)16000m, priceChanged = true, problem = (string?)null },
        },
        quotedTotal = 23000m, total, priceChanged = true, priceChangeRule = "ConfirmNewPrice", canPay = true,
        message = "Some prices have changed.", expiresAt = "2026-11-17T12:00:00Z", paymentUrl = (string?)null,
    };

    [Fact]
    public async Task ReadingSendsNoSessionAndEscapesTheToken()
    {
        var handler = new ControlledHandler(HttpStatusCode.OK, View());
        var view = await Client(handler).GetAsync("a/b c");
        Assert.Equal("/api/checkout/a%2Fb%20c", handler.Path);
        Assert.Null(handler.Authorization);
        Assert.Equal("open", view.State);
        Assert.True(view.Lines[0].PriceChanged);
        Assert.Equal(16000m, view.Lines[0].UnitPrice);
        Assert.Equal(new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc), view.ExpiresAt!.Value.ToUniversalTime());
    }

    [Fact]
    public async Task AnInvalidLinkIsANotFound()
    {
        var handler = new ControlledHandler(HttpStatusCode.NotFound, new { errorCode = "NOT_FOUND", message = "This checkout link isn't valid." });
        var error = await Assert.ThrowsAsync<ApiException>(() => Client(handler).GetAsync("token"));
        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
    }

    [Fact]
    public async Task PayingSendsTheTotalShownAndReturnsThePaymentPage()
    {
        var handler = new ControlledHandler(HttpStatusCode.OK, new { paymentUrl = "https://pay.example.test/x", expiresAt = "2026-11-17T12:15:00Z" });
        var result = await Client(handler).PayAsync("token", 23000.00m);
        Assert.Equal("/api/checkout/token/pay", handler.Path);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal(23000.00m, JsonDocument.Parse(handler.Body!).RootElement.GetProperty("expectedTotal").GetDecimal());
        Assert.Null(handler.Authorization);
        Assert.Equal("https://pay.example.test/x", result.PaymentUrl);
        Assert.Null(result.Checkout);
    }

    [Fact]
    public async Task AChangedCheckoutComesBackToShowInsteadOfThrowing()
    {
        var handler = new ControlledHandler(HttpStatusCode.Conflict, new
        {
            errorCode = "CHECKOUT_CHANGED", message = "The total is now NGN 24,000.00. Check it and press Pay again.", checkout = View(total: 24000m),
        });
        var result = await Client(handler).PayAsync("token", 23000m);
        Assert.Null(result.PaymentUrl);
        Assert.Equal("CHECKOUT_CHANGED", result.ErrorCode);
        Assert.Equal("The total is now NGN 24,000.00. Check it and press Pay again.", result.Message);
        Assert.Equal(24000m, result.Checkout!.Total);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task OtherRefusalsThrowWithTheirStatus(HttpStatusCode status)
    {
        // A conflict without the checkout in it is an error like any other.
        var handler = new ControlledHandler(status, new { errorCode = "CHECKOUT_UNAVAILABLE", message = "Payments are paused. Try again soon." });
        var error = await Assert.ThrowsAsync<ApiException>(() => Client(handler).PayAsync("token", 1m));
        Assert.Equal(status, error.StatusCode);
        Assert.Equal("Payments are paused. Try again soon.", error.UserMessage);
    }

    [Theory]
    [InlineData(24000, "NGN 24,000.00")]
    [InlineData(1234.5, "NGN 1,234.50")]
    public void MoneyIsTheCodeAndTwoPlaces(decimal amount, string expected) => Assert.Equal(expected, CheckoutMoney.Format(amount, "ngn"));

    private static CheckoutClient Client(HttpMessageHandler handler) => new(new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") });

    private sealed class ControlledHandler(HttpStatusCode status, object reply) : HttpMessageHandler
    {
        public string? Path, Body, Authorization;
        public HttpMethod? Method;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Path = request.RequestUri!.AbsolutePath;
            Method = request.Method;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Authorization = request.Headers.Authorization?.ToString();
            return new HttpResponseMessage(status) { Content = JsonContent.Create(reply) };
        }
    }
}
