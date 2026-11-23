using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Parrot.Web.Services;

namespace Parrot.Web.Features.Checkout;

/// <summary>One item on a checkout. UnitPrice and LineTotal are null when the item can't be priced now (gone, say);
/// Problem says what's wrong with it, if anything. ProductId and VariantId name it when the customer changes how
/// many they want, up to MaxQuantity (null once the checkout can't be changed).</summary>
public sealed record CheckoutLine(string Name, string? VariantName, int Quantity, decimal QuotedUnitPrice, decimal? UnitPrice,
    decimal? LineTotal, bool PriceChanged, string? Problem, Guid? ProductId = null, Guid? VariantId = null, int? MaxQuantity = null);

/// <summary>How many of one item the customer wants: 0 takes it out.</summary>
public sealed record CheckoutItemChange(Guid ProductId, Guid? VariantId, int Quantity);

/// <summary>A change to the cart: the checkout as it is now, and why the change didn't go through when it didn't.</summary>
public sealed record CheckoutChange(CheckoutView Checkout, string? Message = null);

/// <summary>
/// A checkout as its customer sees it, from GET api/checkout/{token}. State is "open", "paying", "paid", "expired",
/// "replaced" or "unavailable". QuotedTotal is what the business sent; Total is what it costs now.
/// </summary>
public sealed record CheckoutView(string State, string Business, string? LogoUrl, string? CustomerName, string Currency,
    IReadOnlyList<CheckoutLine> Lines, decimal QuotedTotal, decimal Total, bool PriceChanged, string PriceChangeRule,
    bool CanPay, string? Message, DateTime? ExpiresAt, string? PaymentUrl);

/// <summary>
/// What pressing Pay led to: a payment page to go to (PaymentUrl), or the checkout as it is now (Checkout) with why
/// it wasn't paid (Message), when it changed or was refused since it was shown.
/// </summary>
public sealed record CheckoutPayment(string? PaymentUrl, DateTime? ExpiresAt, CheckoutView? Checkout = null, string? Message = null, string? ErrorCode = null);

/// <summary>
/// A customer's checkout link: no session, the token is the key. Failures other than a changed checkout throw an
/// <see cref="ApiException"/> carrying the API's message (404 for a link that isn't valid, 503 when payments are
/// down, 429 for too many tries).
/// </summary>
public sealed class CheckoutClient(HttpClient http)
{
    public async Task<CheckoutView> GetAsync(string token)
    {
        using HttpResponseMessage response = await http.GetAsync($"api/checkout/{Uri.EscapeDataString(token)}");
        await response.EnsureApiSuccessAsync();
        return await response.Content.ReadFromJsonAsync<CheckoutView>()
            ?? throw new HttpRequestException("The server returned an empty response.");
    }

    /// <summary>Asks for a payment page for the total the customer was shown. A 409 is not thrown: it hands back the
    /// checkout as it is now, to show instead.</summary>
    public async Task<CheckoutPayment> PayAsync(string token, decimal expectedTotal)
    {
        using HttpResponseMessage response = await http.PostAsJsonAsync($"api/checkout/{Uri.EscapeDataString(token)}/pay", new { expectedTotal });
        if (response.StatusCode == HttpStatusCode.Conflict && await ChangedAsync(response) is { } changed) return changed;

        await response.EnsureApiSuccessAsync();
        return await response.Content.ReadFromJsonAsync<CheckoutPayment>()
            ?? throw new HttpRequestException("The server returned an empty response.");
    }

    /// <summary>Sets how many of each item the customer wants (every item kept, 0 to take one out). A 409 is not
    /// thrown: it hands back the checkout unchanged, with why.</summary>
    public async Task<CheckoutChange> ChangeAsync(string token, IReadOnlyList<CheckoutItemChange> items)
    {
        using HttpResponseMessage response = await http.PutAsJsonAsync($"api/checkout/{Uri.EscapeDataString(token)}/items", new { items });
        if (response.StatusCode == HttpStatusCode.Conflict && await ChangedAsync(response) is { Checkout: { } kept } refused)
        {
            return new CheckoutChange(kept, refused.Message);
        }

        await response.EnsureApiSuccessAsync();
        return new CheckoutChange(await response.Content.ReadFromJsonAsync<CheckoutView>()
            ?? throw new HttpRequestException("The server returned an empty response."));
    }

    // A conflict without the checkout in it (a proxy's page, say) is thrown as the error it is.
    private static async Task<CheckoutPayment?> ChangedAsync(HttpResponseMessage response)
    {
        try
        {
            string body = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<CheckoutPayment>(body, Json) is { Checkout: not null } changed ? changed : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

/// <summary>Money on a checkout: the currency's code and the amount to two places, as the API words it in its
/// messages ("NGN 24,000.00"), so the page and the messages agree.</summary>
public static class CheckoutMoney
{
    public static string Format(decimal amount, string? currency) =>
        $"{(string.IsNullOrWhiteSpace(currency) ? "NGN" : currency.Trim().ToUpperInvariant())} {amount.ToString("#,##0.00", CultureInfo.InvariantCulture)}";
}
