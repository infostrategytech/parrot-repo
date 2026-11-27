using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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
public sealed partial class AccelerateClient(HttpClient http, AccelerateSession session, IOptions<PaymentSettings> settings, ILogger<AccelerateClient> logger) : IPaymentProvider
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
        decimal payAmount = amountMinor / 100m;
        object body = new { PayAmount = payAmount, Reference = reference, CallbackUrl = callback, Description = description };

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
            string raw = await response.Content.ReadAsStringAsync(ct);
            Answer<TicketData>? answer = Parse<Answer<TicketData>>(raw);
            if (response.IsSuccessStatusCode && answer is { Success: true, OperationCode: Success, Data: { SessionToken.Length: > 0, RedirectUrl.Length: > 0 } data })
            {
                return new PaymentLink(data.SessionToken, PayPage(data.RedirectUrl, reference));
            }

            string code = answer?.OperationCode ?? ((int)response.StatusCode).ToString();
            // Everything that went, but the keys, the bearer token and the checkout token in the callback, and everything
            // that came back: Accelerate's message is often only "Unknown Error", so the request is what tells why.
            logger.LogWarning(
                "Accelerate refused the payment link. Status={Status} Code={Code} Message={Message} Host={Host} PayAmount={PayAmount} Reference={Reference} "
                + "Description={Description} Callback={Callback} CallbackLength={CallbackLength} Body={Body}",
                (int)response.StatusCode, code, answer?.Message, http.BaseAddress?.Host, payAmount, reference,
                description, Shape(callback), callback.Length, ForLog(raw));
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
        string raw = await response.Content.ReadAsStringAsync(ct);
        LoginAnswer? answer = Parse<LoginAnswer>(raw);
        if (response.IsSuccessStatusCode && answer?.Data?.Token is { Length: > 0 } token)
        {
            return token;
        }

        logger.LogError("Accelerate login failed. Status={Status} GotToken={GotToken} Message={Message} Url={Url} Body={Body}",
            (int)response.StatusCode, answer?.Data?.Token is { Length: > 0 }, answer?.Message, request.RequestUri, ForLog(raw));
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

    private static T? Parse<T>(string raw)
        where T : class
    {
        try
        {
            return string.IsNullOrWhiteSpace(raw) ? null : JsonSerializer.Deserialize<T>(raw, Json);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private const int MaxLoggedBody = 2000;

    /// <summary>An answer for the log: any token in it hidden, and cut short.</summary>
    public static string ForLog(string raw)
    {
        string hidden = TokenValue().Replace(raw, m => $"\"{m.Groups["name"].Value}\":\"(hidden)\"");
        return hidden.Length <= MaxLoggedBody ? hidden : hidden[..MaxLoggedBody] + "…";
    }

    /// <summary>
    /// A callback for the log without what can open the checkout: its site and the first part of its path
    /// ("https://app.example/checkout/…"), and whether it has a query.
    /// </summary>
    public static string Shape(string callback)
    {
        if (!Uri.TryCreate(callback, UriKind.Absolute, out Uri? uri))
        {
            return "(not an absolute URL)";
        }

        string[] path = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string first = path.Length == 0 ? "/" : $"/{path[0]}" + (path.Length > 1 ? "/…" : "");
        return $"{uri.Scheme}://{uri.Authority}{first}" + (uri.Query.Length > 1 ? " (with a query)" : "");
    }

    // "token": "…", "session_token": "…", "access_token": "…" and the like.
    [GeneratedRegex(@"""(?<name>[A-Za-z_]*token)""\s*:\s*""[^""]*""", RegexOptions.IgnoreCase)]
    private static partial Regex TokenValue();

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
