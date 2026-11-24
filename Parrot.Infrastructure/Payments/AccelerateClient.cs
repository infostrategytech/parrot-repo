using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Parrot.Application.Payments;

namespace Parrot.Infrastructure.Payments;

/// <summary>
/// The platform's Accelerate account (iStrategy's payments API). It trades the account's
/// public and secret keys (Basic auth, on the user-management API) for a bearer token that lasts a day, keeps it in
/// <see cref="AccelerateSession"/>, and logs in again when it's refused.
/// Docs: https://istrategytech.gitbook.io/accelerate-1/api-documentation/payments/payment-links
/// </summary>
public sealed class AccelerateClient(HttpClient http, AccelerateSession session, IOptions<PaymentSettings> settings, ILogger<AccelerateClient> logger) : IPaymentProvider
{
    public const string ProviderName = "accelerate";

    /// <summary>Accelerate's code for success.</summary>
    private const string Success = "0000";

    private static readonly JsonSerializerOptions Json = new () { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true };

    public string Name => ProviderName;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Keys.PublicKey) && !string.IsNullOrWhiteSpace(Keys.SecretKey) && http.BaseAddress is not null
        && Uri.TryCreate(Keys.AuthBaseUrl, UriKind.Absolute, out _);

    private AccelerateSettings Keys => settings.Value.Accelerate;

    public async Task<PaymentLink> CreateLinkAsync(string reference, long amountMinor, string description, string? returnUrl, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            throw new PaymentProviderException("Accelerate isn't configured.");
        }

        if (amountMinor < 1)
        {
            throw new PaymentProviderException("The amount must be at least 1 kobo.");
        }

        string? callback = string.IsNullOrWhiteSpace(returnUrl) ? settings.Value.CallbackUrl : returnUrl;
        if (string.IsNullOrWhiteSpace(callback))
        {
            throw new PaymentProviderException("There's no page to send the customer back to after paying.");
        }

        // Accelerate takes the amount in naira ("pay_amount": 800 is ₦800), not kobo.
        object body = new { PayAmount = amountMinor / 100m, Reference = reference, CallbackUrl = callback, Description = description };

        // A token the account no longer accepts is replaced once.
        HttpResponseMessage response = await SendAsync(body, await session.TokenAsync(LoginAsync, ct), ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            session.Forget();
            response = await SendAsync(body, await session.TokenAsync(LoginAsync, ct), ct);
        }

        using (response)
        {
            Answer<TicketData>? answer = await ReadAsync<Answer<TicketData>>(response, ct);
            if (response.IsSuccessStatusCode && answer is { Success: true, OperationCode: Success, Data: { SessionToken.Length: > 0, RedirectUrl.Length: > 0 } data })
            {
                return new PaymentLink(data.SessionToken, PayPage(data.RedirectUrl, reference));
            }

            string code = answer?.OperationCode ?? ((int)response.StatusCode).ToString();
            logger.LogWarning("Accelerate refused the payment link. Status={Status} Code={Code} Message={Message}", (int)response.StatusCode, code, answer?.Message);
            throw new PaymentProviderException(response.StatusCode == HttpStatusCode.TooManyRequests
                ? "Accelerate's rate limit was reached."
                : answer?.Message ?? $"Accelerate answered {(int)response.StatusCode}.", code);
        }
    }

    /// <summary>
    /// Accelerate answers with its payment page and an empty query ("https://accelerate.ng/payment?"); the page finds
    /// the payment from ?ref=, which takes our reference (or the session token, a 1,300-character JWT).
    /// </summary>
    private static string PayPage(string redirectUrl, string reference)
    {
        if (redirectUrl.Contains("ref=", StringComparison.OrdinalIgnoreCase)) return redirectUrl;
        string separator = redirectUrl.EndsWith('?') || redirectUrl.EndsWith('&') ? "" : redirectUrl.Contains('?') ? "&" : "?";
        return $"{redirectUrl}{separator}ref={Uri.EscapeDataString(reference)}";
    }

    private async Task<HttpResponseMessage> SendAsync(object body, string token, CancellationToken ct)
    {
        using HttpRequestMessage request = new (HttpMethod.Post, "api/merchant/create-ticket");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(body, options: Json);
        return await Guard(() => http.SendAsync(request, ct), ct);
    }

    private async Task<string> LoginAsync(CancellationToken ct)
    {
        // An API client's token comes from Accelerate's user-management API, not the payments host the client is
        // based on: GET with the public and secret keys as Basic credentials.
        using HttpRequestMessage request = new (HttpMethod.Get, new Uri(new Uri(Keys.AuthBaseUrl.TrimEnd('/') + "/"), "auth/api-client/token"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Keys.PublicKey}:{Keys.SecretKey}")));
        using HttpResponseMessage response = await Guard(() => http.SendAsync(request, ct), ct);
        LoginAnswer? answer = await ReadAsync<LoginAnswer>(response, ct);
        if (response.IsSuccessStatusCode && answer?.Data?.Token is { Length: > 0 } token)
        {
            return token;
        }

        logger.LogError("Accelerate login failed. Status={Status} GotToken={GotToken} Message={Message}", (int)response.StatusCode, answer?.Data?.Token is { Length: > 0 }, answer?.Message);
        throw new PaymentProviderException("Accelerate wouldn't log in with the configured keys.", ((int)response.StatusCode).ToString());
    }

    private static async Task<HttpResponseMessage> Guard(Func<Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        try
        {
            return await send();
        }
        catch (HttpRequestException ex)
        {
            throw new PaymentProviderException($"Accelerate can't be reached: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new PaymentProviderException("Accelerate took too long to answer.");
        }
    }

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
        where T : class
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, ct);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    // Only the fields used are read, so a field we ignore (login's status, the token's
    // expires_in) can't fail the parse by arriving in a different type.
    // Create-ticket answers {success, message, operation_code, data}.
    private sealed record Answer<T>(bool Success, string? Message, string? OperationCode, T? Data);

    // Login answers {status, data: {token, expires_in}}.
    private sealed record LoginAnswer(LoginData? Data, string? Message);

    private sealed record LoginData(string? Token);

    private sealed record TicketData(string? SessionToken, string? RedirectUrl, string? Reference);
}

/// <summary>
/// The account's bearer token, shared by every request. Accelerate says it lasts 24 hours;
/// it's renewed after 23, or straight away when Accelerate refuses it.
/// </summary>
public sealed class AccelerateSession(TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(23);

    private readonly SemaphoreSlim _gate = new (1, 1);
    private string? _token;
    private DateTimeOffset _until;

    public async Task<string> TokenAsync(Func<CancellationToken, Task<string>> login, CancellationToken ct)
    {
        if (_token is { } current && clock.GetUtcNow() < _until)
        {
            return current;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_token is { } fresh && clock.GetUtcNow() < _until)
            {
                return fresh;
            }

            _token = await login(ct);
            _until = clock.GetUtcNow().Add(Lifetime);
            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Forget() => _token = null;
}
