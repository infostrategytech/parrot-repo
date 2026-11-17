using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Parrot.Web.Features.Auth.State;
using Parrot.Web.Services;

namespace Parrot.Web.Features.Settings;

/// <summary>The ranges the API accepts, in minutes.</summary>
public sealed record PaymentLimits(int MinCheckoutMinutes, int MaxCheckoutMinutes, int MinPaymentMinutes, int MaxPaymentMinutes)
{
    public static PaymentLimits Defaults { get; } = new(5, 1440, 5, 120);
}

/// <summary>
/// How the checkout links agents send behave, from GET api/payments/settings. HoldMode is "AtPay", "AtCheckout"
/// or "Never"; PriceChangeRule is "ConfirmNewPrice" or "Block". IsDefault: nothing saved yet, so these are the
/// platform's.
/// </summary>
public sealed record PaymentSettings(int CheckoutMinutes, string HoldMode, int PaymentMinutes, string PriceChangeRule,
    bool IsDefault = false, DateTime? UpdatedAt = null, PaymentLimits? Limits = null);

/// <summary>
/// The session's workspace's checkout and payment settings. Failures throw an <see cref="ApiException"/>
/// carrying the API's message (a 400 for a value out of range, a 403 for someone who may not change them).
/// </summary>
public sealed class PaymentSettingsClient(HttpClient http, AuthState authState)
{
    public async Task<PaymentSettings> ReadAsync()
    {
        using HttpRequestMessage request = Authorized(HttpMethod.Get, "api/payments/settings");
        using HttpResponseMessage response = await http.SendAsync(request);
        return await ReadAsync<PaymentSettings>(response);
    }

    /// <summary>Saves the four choices; the API answers with what it kept.</summary>
    public async Task<PaymentSettings> SaveAsync(PaymentSettings settings)
    {
        using HttpRequestMessage request = Authorized(HttpMethod.Put, "api/payments/settings");
        request.Content = JsonContent.Create(new { settings.CheckoutMinutes, settings.HoldMode, settings.PaymentMinutes, settings.PriceChangeRule });
        using HttpResponseMessage response = await http.SendAsync(request);
        return await ReadAsync<PaymentSettings>(response);
    }

    private HttpRequestMessage Authorized(HttpMethod method, string path)
    {
        string token = authState.CurrentUser?.Token
            ?? throw new InvalidOperationException("Payment settings are only reachable while signed in.");

        HttpRequestMessage request = new(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        await response.EnsureApiSuccessAsync();
        return await response.Content.ReadFromJsonAsync<T>()
            ?? throw new HttpRequestException("The server returned an empty response.");
    }
}

/// <summary>The choices the Payments section offers, in the owner's words.</summary>
public static class PaymentChoices
{
    public static IReadOnlyList<int> CheckoutPresets { get; } = [15, 30, 60, 120, 360, 720, 1440];
    public static IReadOnlyList<int> PaymentPresets { get; } = [5, 10, 15, 30, 60, 120];

    public static IReadOnlyList<(string Value, string Label)> HoldModes { get; } =
    [
        ("AtPay", "When the customer presses Pay"),
        ("AtCheckout", "As soon as the checkout is sent"),
        ("Never", "Don't keep stock aside"),
    ];

    public static IReadOnlyList<(string Value, string Label)> PriceChangeRules { get; } =
    [
        ("ConfirmNewPrice", "Show old and new price; customer confirms"),
        ("Block", "Customer must ask for a new checkout"),
    ];

    public static string HoldModeLabel(string? value) => HoldModes.FirstOrDefault(mode => mode.Value == value).Label ?? HoldModes[0].Label;
    public static string PriceChangeLabel(string? value) => PriceChangeRules.FirstOrDefault(rule => rule.Value == value).Label ?? PriceChangeRules[0].Label;

    /// <summary>What happens with each hold choice, said once under the field.</summary>
    public static string HoldModeHelp(string? value) => value switch
    {
        "AtCheckout" => "Stock is locked for everyone who gets a checkout, even people who never pay.",
        "Never" => "Nothing is kept aside, so two people could pay for the last one.",
        _ => "Recommended. Stock is kept aside only for people ready to pay.",
    };

    /// <summary>The presets inside the limits, with the current value added when it isn't one of them (set elsewhere).</summary>
    public static IReadOnlyList<int> Options(IReadOnlyList<int> presets, int min, int max, int current) =>
        [.. presets.Append(current).Where(minutes => minutes >= min && minutes <= max).Distinct().Order()];

    /// <summary>"15 minutes", "1 hour", "1 hour 30 minutes", "24 hours".</summary>
    public static string Duration(int minutes)
    {
        int hours = minutes / 60, rest = minutes % 60;
        string Part(int count, string unit) => count.ToString(CultureInfo.InvariantCulture) + " " + unit + (count == 1 ? "" : "s");
        if (hours == 0) return Part(rest, "minute");
        return rest == 0 ? Part(hours, "hour") : $"{Part(hours, "hour")} {Part(rest, "minute")}";
    }
}
