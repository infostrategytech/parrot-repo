using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Parrot.Application.Conversations;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;

namespace Parrot.Infrastructure.Conversations;

// Messenger's Send API: one text reply from the Page to a customer, as a RESPONSE to their message
// (allowed within 24 hours of it). No automatic retry, as for WhatsApp: a timeout leaves the outcome
// unknown, and sending again could deliver twice.
public sealed class FacebookSender(HttpClient http, IOptions<FacebookSettings> options) : IFacebookSender
{
    public Task<string> SendAsync(string pageId, string pageAccessToken, string recipient, string text, CancellationToken ct) =>
        SendTextAsync(pageId, pageAccessToken, recipient, text, null, ct);

    public Task<string> SendReplyAsync(string pageId, string pageAccessToken, string recipient, string text, string replyTo, CancellationToken ct) =>
        SendTextAsync(pageId, pageAccessToken, recipient, text, replyTo, ct);

    public Task<string> SendMediaAsync(string pageId, string pageAccessToken, string recipient, string kind, string contentType,
        string fileName, Stream content, CancellationToken ct) =>
        SendFileAsync(pageId, pageAccessToken, recipient, kind, contentType, fileName, content, null, ct);

    public Task<string> SendMediaReplyAsync(string pageId, string pageAccessToken, string recipient, string kind, string contentType,
        string fileName, Stream content, string replyTo, CancellationToken ct) =>
        SendFileAsync(pageId, pageAccessToken, recipient, kind, contentType, fileName, content, replyTo, ct);

    // A reply that quotes a message carries reply_to.mid at the top level (either side's message).
    private async Task<string> SendTextAsync(string pageId, string pageAccessToken, string recipient, string text, string? replyTo, CancellationToken ct)
    {
        FacebookSettings settings = options.Value;
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post,
            $"https://graph.facebook.com/{Uri.EscapeDataString(settings.ApiVersion)}/{Uri.EscapeDataString(pageId)}/messages");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pageAccessToken);
        request.Content = JsonContent.Create(new Dictionary<string, object?>
        {
            ["recipient"] = new { id = recipient },
            ["messaging_type"] = "RESPONSE",
            ["message"] = new { text },
        }.Quoting(replyTo));
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
                new KeyValuePair<string, object?>("endpoint", "page_messages"));
        }

        using HttpResponseMessage disposeResponse = response;
        if ((int)response.StatusCode >= 500) throw new HttpRequestException("Messenger send outcome is uncertain.");
        if (!response.IsSuccessStatusCode) throw new FacebookRejectedException(await ErrorCodeAsync(response, ct));
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!json.RootElement.TryGetProperty("message_id", out JsonElement id) || id.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(id.GetString()))
            throw new JsonException("Messenger returned no message ID.");
        return id.GetString()!;
    }

    private async Task<string> SendFileAsync(string pageId, string pageAccessToken, string recipient, string kind, string contentType,
        string fileName, Stream content, string? replyTo, CancellationToken ct)
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post,
            $"https://graph.facebook.com/{Uri.EscapeDataString(options.Value.ApiVersion)}/{Uri.EscapeDataString(pageId)}/messages");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pageAccessToken);
        StreamContent file = new(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        MultipartFormDataContent form = new()
        {
            { new StringContent(JsonSerializer.Serialize(new { id = recipient })), "recipient" },
            { new StringContent("RESPONSE"), "messaging_type" },
            { new StringContent(JsonSerializer.Serialize(new { attachment = new { type = kind == "image" ? "image" : "file", payload = new { is_reusable = false } } })), "message" },
        };
        if (replyTo is not null) form.Add(new StringContent(JsonSerializer.Serialize(new { mid = replyTo })), "reply_to");
        form.Add(file, "filedata", fileName);
        request.Content = form;
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
                new KeyValuePair<string, object?>("endpoint", "page_messages"));
        }

        using HttpResponseMessage disposeResponse = response;
        if ((int)response.StatusCode >= 500) throw new HttpRequestException("Messenger send outcome is uncertain.");
        if (!response.IsSuccessStatusCode) throw new FacebookRejectedException(await ErrorCodeAsync(response, ct));
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!json.RootElement.TryGetProperty("message_id", out JsonElement id) || id.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(id.GetString()))
            throw new JsonException("Messenger returned no message ID.");
        return id.GetString()!;
    }

    public async Task MarkSeenAsync(string pageId, string pageAccessToken, string recipient, CancellationToken ct)
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post,
            $"https://graph.facebook.com/{Uri.EscapeDataString(options.Value.ApiVersion)}/{Uri.EscapeDataString(pageId)}/messages");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pageAccessToken);
        request.Content = JsonContent.Create(new { recipient = new { id = recipient }, sender_action = "mark_seen" });
        using HttpResponseMessage response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Messenger refused to mark the message seen (error {await ErrorCodeAsync(response, ct) ?? "unknown"}).");
    }

    // Meta's {"error":{"code":10,...}}: a fixed set of codes, safe as a metric label.
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
