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
    public async Task<string> SendAsync(string phoneNumberId, string accessToken, string recipient, string text, CancellationToken ct)
    {
        WhatsAppSettings settings = options.Value;
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post,
            $"https://graph.facebook.com/{Uri.EscapeDataString(settings.ApiVersion)}/{Uri.EscapeDataString(phoneNumberId)}/messages");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(new
        {
            messaging_product = "whatsapp", recipient_type = "individual", to = recipient,
            type = "text", text = new { body = text },
        });
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
