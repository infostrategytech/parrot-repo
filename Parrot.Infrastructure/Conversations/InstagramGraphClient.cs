using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Parrot.Application.Conversations;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;

namespace Parrot.Infrastructure.Conversations;

// graph.instagram.com, with the business's Instagram User access token (Instagram API with Instagram Login).
internal static class InstagramGraph
{
    public static string Url(IOptions<InstagramSettings> options, string path) =>
        $"https://graph.instagram.com/{Uri.EscapeDataString(options.Value.ApiVersion)}/{path}";

    // Meta's {"error":{"code":190,...}}: a fixed set of codes, safe as a metric label.
    public static async Task<string?> ErrorCodeAsync(HttpResponseMessage response, CancellationToken ct)
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

// The two setup calls: who the token belongs to, and subscribing that account to message webhooks.
public sealed class InstagramAccountApi(HttpClient http, IOptions<InstagramSettings> options) : IInstagramAccountApi
{
    public async Task<InstagramAccount> GetAccountAsync(string accessToken, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, InstagramGraph.Url(options, "me?fields=user_id,username"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using HttpResponseMessage response = await SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InstagramAccountException(Refused(await InstagramGraph.ErrorCodeAsync(response, cancellationToken)));

        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        // user_id is the professional account webhooks name in entry.id; it can come back as a number or a string.
        string? accountId = json.RootElement.TryGetProperty("user_id", out JsonElement id)
            ? id.ValueKind switch
            {
                JsonValueKind.String => id.GetString(),
                JsonValueKind.Number => id.GetRawText(),
                _ => null,
            }
            : null;
        string username = json.RootElement.TryGetProperty("username", out JsonElement name) && name.ValueKind == JsonValueKind.String
            ? name.GetString()! : "";
        if (string.IsNullOrEmpty(accountId) || !accountId.All(char.IsAsciiDigit))
            throw new InstagramAccountException("Instagram did not say which account this token belongs to. Generate the token under Instagram > API setup with Instagram login.");
        return new InstagramAccount(accountId, username);
    }

    public async Task SubscribeToMessagesAsync(string accessToken, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, InstagramGraph.Url(options, "me/subscribed_apps?subscribed_fields=messages"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using HttpResponseMessage response = await SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InstagramAccountException(
                "Instagram accepted the token but would not send this account's messages to Parrot. Check the token has instagram_business_manage_messages. "
                + Refused(await InstagramGraph.ErrorCodeAsync(response, cancellationToken)));
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException)
        {
            throw new InstagramAccountException("Parrot could not reach Instagram. Try again in a moment.");
        }
    }

    private static string Refused(string? code) => code switch
    {
        "190" => "Instagram did not accept this access token: it is wrong or has expired. Generate a new one.",
        null => "Instagram refused the request.",
        _ => $"Instagram refused the request (error {code}).",
    };
}

// Instagram's Send API: one text reply from the account to a customer, within 24 hours of their message.
// No automatic retry: a timeout leaves the outcome unknown, and sending again could deliver twice.
public sealed class InstagramSender(HttpClient http, IOptions<InstagramSettings> options) : IInstagramSender
{
    public async Task<string> SendAsync(string accountId, string accessToken, string recipient, string text, CancellationToken ct)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, InstagramGraph.Url(options, $"{Uri.EscapeDataString(accountId)}/messages"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(new
        {
            recipient = new { id = recipient },
            message = new { text },
        });
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        finally
        {
            MessageMetrics.MetaApiDuration.Record(
                System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds,
                new KeyValuePair<string, object?>("endpoint", "instagram_messages"));
        }

        using HttpResponseMessage disposeResponse = response;
        if ((int)response.StatusCode >= 500) throw new HttpRequestException("Instagram send outcome is uncertain.");
        if (!response.IsSuccessStatusCode) throw new InstagramRejectedException(await InstagramGraph.ErrorCodeAsync(response, ct));
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!json.RootElement.TryGetProperty("message_id", out JsonElement id) || id.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(id.GetString()))
            throw new JsonException("Instagram returned no message ID.");
        return id.GetString()!;
    }
}
