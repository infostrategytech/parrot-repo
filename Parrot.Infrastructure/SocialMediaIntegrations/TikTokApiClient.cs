using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Parrot.Application.Integrations;

namespace Parrot.Infrastructure.SocialMediaIntegrations;

// TikTok API for Business over HTTP. Every response is {"code": 0, "message": "OK", "data": {...}};
// a non-zero code is a refusal even when the HTTP status is 200.
public sealed class TikTokApiClient(HttpClient http, IOptions<TikTokSettings> options) : ITikTokApiClient
{
    public async Task RegisterWebhookAsync(string appId, string appSecret, string callbackUrl, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, Url("business/webhook/update/"))
        {
            Content = JsonContent.Create(new
            {
                app_id = appId, secret = appSecret, event_type = "DIRECT_MESSAGE", callback_url = callbackUrl,
            }),
        };
        using JsonDocument _ = await SendAsync(request, cancellationToken);
    }

    public async Task<TikTokTokens> ExchangeCodeAsync(string appId, string appSecret, string code, string redirectUri,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, Url("tt_user/oauth2/token/"))
        {
            Content = JsonContent.Create(new
            {
                client_id = appId, client_secret = appSecret, grant_type = "authorization_code",
                auth_code = code, redirect_uri = redirectUri,
            }),
        };
        using JsonDocument json = await SendAsync(request, cancellationToken);
        JsonElement data = Data(json);
        return new TikTokTokens(
            RequiredString(data, "open_id"),
            (OptionalString(data, "scope") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            RequiredString(data, "access_token"),
            TimeSpan.FromSeconds(RequiredSeconds(data, "expires_in")),
            RequiredString(data, "refresh_token"),
            TimeSpan.FromSeconds(RequiredSeconds(data, "refresh_token_expires_in")));
    }

    public async Task<TikTokAccount> GetAccountAsync(string businessId, string accessToken, CancellationToken cancellationToken)
    {
        // TikTok takes the field list as a JSON array.
        string fields = JsonSerializer.Serialize(new[] { "username", "display_name" });
        using HttpRequestMessage request = new(HttpMethod.Get,
            Url($"business/get/?business_id={Uri.EscapeDataString(businessId)}&fields={Uri.EscapeDataString(fields)}"));
        request.Headers.Add("Access-Token", accessToken);
        using JsonDocument json = await SendAsync(request, cancellationToken);
        JsonElement data = Data(json);
        return new TikTokAccount(RequiredString(data, "username"), OptionalString(data, "display_name"));
    }

    private string Url(string path) =>
        $"https://business-api.tiktok.com/open_api/{Uri.EscapeDataString(options.Value.ApiVersion)}/{path}";

    // A 5xx or no answer is an HttpRequestException (worth a retry); a 4xx or non-zero code is a TikTokApiException.
    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        if ((int)response.StatusCode >= 500)
            throw new HttpRequestException($"TikTok answered {(int)response.StatusCode}.", null, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(body);
        }
        catch (JsonException) when (!response.IsSuccessStatusCode)
        {
            throw new TikTokApiException((int)response.StatusCode, $"TikTok answered {(int)response.StatusCode}.");
        }

        long code = json.RootElement.ValueKind == JsonValueKind.Object
            && json.RootElement.TryGetProperty("code", out JsonElement codeElement) && codeElement.TryGetInt64(out long value)
                ? value
                : -1;
        if (code == 0 && response.IsSuccessStatusCode) return json;

        string message = json.RootElement.ValueKind == JsonValueKind.Object
            && json.RootElement.TryGetProperty("message", out JsonElement messageElement)
            && messageElement.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(messageElement.GetString())
                ? messageElement.GetString()!
                : $"TikTok answered {(int)response.StatusCode}.";
        json.Dispose();
        throw new TikTokApiException(code == -1 ? (int)response.StatusCode : code, message);
    }

    private static JsonElement Data(JsonDocument json) =>
        json.RootElement.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Object
            ? data
            : throw new JsonException("TikTok returned no data.");

    private static string? OptionalString(JsonElement data, string name) =>
        data.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string RequiredString(JsonElement data, string name) =>
        OptionalString(data, name) is { Length: > 0 } value ? value : throw new JsonException($"TikTok returned no {name}.");

    // Lifetimes in seconds; accepted as a number or a numeric string.
    private static long RequiredSeconds(JsonElement data, string name)
    {
        if (data.TryGetProperty(name, out JsonElement value))
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long seconds) && seconds > 0) return seconds;
            if (value.ValueKind == JsonValueKind.String
                && long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out seconds) && seconds > 0)
                return seconds;
        }

        throw new JsonException($"TikTok returned no {name}.");
    }
}
