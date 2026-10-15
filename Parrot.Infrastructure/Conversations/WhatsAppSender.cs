using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Parrot.Application.Conversations;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;

namespace Parrot.Infrastructure.Conversations;

public sealed class WhatsAppSender(HttpClient http, IOptions<WhatsAppSettings> options) : IWhatsAppSender
{
    public Task<string> SendAsync(string phoneNumberId, string accessToken, string recipient, string text, CancellationToken ct) =>
        SendTextAsync(phoneNumberId, accessToken, recipient, text, null, ct);

    public Task<string> SendReplyAsync(string phoneNumberId, string accessToken, string recipient, string text, string replyTo, CancellationToken ct) =>
        SendTextAsync(phoneNumberId, accessToken, recipient, text, replyTo, ct);

    public Task<string> SendMediaAsync(string phoneNumberId, string accessToken, string recipient, string kind, string contentType,
        string fileName, Stream content, string? caption, CancellationToken ct) =>
        SendFileAsync(phoneNumberId, accessToken, recipient, kind, contentType, fileName, content, caption, null, ct);

    public Task<string> SendMediaReplyAsync(string phoneNumberId, string accessToken, string recipient, string kind, string contentType,
        string fileName, Stream content, string? caption, string replyTo, CancellationToken ct) =>
        SendFileAsync(phoneNumberId, accessToken, recipient, kind, contentType, fileName, content, caption, replyTo, ct);

    // A reply that quotes a message carries context.message_id: the customer sees the quote above it. Added
    // only then: the serializer's ignore-nulls setting does not apply to a dictionary's entries.
    private static Dictionary<string, object?> Quoting(Dictionary<string, object?> body, string? replyTo)
    {
        if (replyTo is not null) body["context"] = new { message_id = replyTo };
        return body;
    }

    private async Task<string> SendTextAsync(string phoneNumberId, string accessToken, string recipient, string text, string? replyTo, CancellationToken ct)
    {
        WhatsAppSettings settings = options.Value;
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post,
            $"https://graph.facebook.com/{Uri.EscapeDataString(settings.ApiVersion)}/{Uri.EscapeDataString(phoneNumberId)}/messages");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(Quoting(new Dictionary<string, object?>
        {
            ["messaging_product"] = "whatsapp", ["recipient_type"] = "individual", ["to"] = recipient,
            ["type"] = "text", ["text"] = new { body = text },
        }, replyTo), options: IgnoreNulls);
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        finally
        {
            // Failed and timed-out calls too: they are the slow ones.
            MessageMetrics.MetaApiDuration.Record(
                System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds,
                new KeyValuePair<string, object?>("endpoint", "messages"));
        }

        using HttpResponseMessage disposeResponse = response;
        if ((int)response.StatusCode >= 500) throw new HttpRequestException("WhatsApp send outcome is uncertain.");
        if (!response.IsSuccessStatusCode) throw new WhatsAppRejectedException(await ErrorCodeAsync(response, ct));
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!json.RootElement.TryGetProperty("messages", out JsonElement messages) || messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() == 0
            || !messages[0].TryGetProperty("id", out JsonElement id) || id.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(id.GetString()))
            throw new JsonException("WhatsApp returned no message ID.");
        return id.GetString()!;
    }

    private static readonly JsonSerializerOptions IgnoreNulls =
        new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private async Task<string> SendFileAsync(string phoneNumberId, string accessToken, string recipient, string kind, string contentType,
        string fileName, Stream content, string? caption, string? replyTo, CancellationToken ct)
    {
        string version = Uri.EscapeDataString(options.Value.ApiVersion);
        string messages = $"https://graph.facebook.com/{version}/{Uri.EscapeDataString(phoneNumberId)}/messages";

        // Step one: the bytes, which WhatsApp keeps and answers with an id. Nothing is sent to the customer yet,
        // so a failure here is a plain refusal and never an uncertain delivery.
        using HttpRequestMessage upload = new(HttpMethod.Post, $"https://graph.facebook.com/{version}/{Uri.EscapeDataString(phoneNumberId)}/media");
        upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        StreamContent file = new(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        upload.Content = new MultipartFormDataContent { { new StringContent("whatsapp"), "messaging_product" }, { new StringContent(contentType), "type" }, { file, "file", fileName } };
        string mediaId;
        HttpResponseMessage uploadResponse;
        try
        {
            uploadResponse = await SendTimedAsync(upload, "media", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // Nothing has reached the customer, so a dropped connection here is a refusal the agent can simply retry.
            throw new WhatsAppRejectedException("upload");
        }
        using (HttpResponseMessage uploaded = uploadResponse)
        {
            if ((int)uploaded.StatusCode >= 500) throw new WhatsAppRejectedException("upload");
            if (!uploaded.IsSuccessStatusCode) throw new WhatsAppRejectedException(await ErrorCodeAsync(uploaded, ct));
            using JsonDocument json = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync(ct));
            mediaId = json.RootElement.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(id.GetString())
                ? id.GetString()!
                : throw new WhatsAppRejectedException("upload");
        }

        // Step two: the message that points at it.
        bool image = kind == "image";
        object media = image
            ? new { id = mediaId, caption }
            : new { id = mediaId, caption, filename = fileName };
        using HttpRequestMessage send = new(HttpMethod.Post, messages);
        send.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        send.Content = JsonContent.Create(Quoting(new Dictionary<string, object?>
        {
            ["messaging_product"] = "whatsapp", ["recipient_type"] = "individual", ["to"] = recipient,
            ["type"] = image ? "image" : "document", [image ? "image" : "document"] = media,
        }, replyTo), options: IgnoreNulls);
        using HttpResponseMessage response = await SendTimedAsync(send, "messages", ct);
        if ((int)response.StatusCode >= 500) throw new HttpRequestException("WhatsApp send outcome is uncertain.");
        if (!response.IsSuccessStatusCode) throw new WhatsAppRejectedException(await ErrorCodeAsync(response, ct));
        using JsonDocument sent = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!sent.RootElement.TryGetProperty("messages", out JsonElement list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0
            || !list[0].TryGetProperty("id", out JsonElement messageId) || messageId.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(messageId.GetString()))
            throw new JsonException("WhatsApp returned no message ID.");
        return messageId.GetString()!;
    }

    private async Task<HttpResponseMessage> SendTimedAsync(HttpRequestMessage request, string endpoint, CancellationToken ct)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            return await http.SendAsync(request, ct);
        }
        finally
        {
            MessageMetrics.MetaApiDuration.Record(
                System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds,
                new KeyValuePair<string, object?>("endpoint", endpoint));
        }
    }

    public async Task MarkReadAsync(string phoneNumberId, string accessToken, string messageId, CancellationToken ct)
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post,
            $"https://graph.facebook.com/{Uri.EscapeDataString(options.Value.ApiVersion)}/{Uri.EscapeDataString(phoneNumberId)}/messages");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(new { messaging_product = "whatsapp", status = "read", message_id = messageId });
        using HttpResponseMessage response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"WhatsApp refused to mark the message read (error {await ErrorCodeAsync(response, ct) ?? "unknown"}).");
    }

    // Meta's {"error":{"code":131047,...}}: a fixed set of codes, safe as a metric label.
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
