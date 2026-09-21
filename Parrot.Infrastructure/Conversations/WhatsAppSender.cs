using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Parrot.Application.Conversations;
using Parrot.Application.Integrations;

namespace Parrot.Infrastructure.Conversations;

public sealed class WhatsAppSender(HttpClient http, IOptions<WhatsAppSettings> options) : IWhatsAppSender
{
    public async Task<string> SendAsync(string phoneNumberId, string recipient, string text, CancellationToken ct)
    {
        WhatsAppSettings settings = options.Value;
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post,
            $"https://graph.facebook.com/{Uri.EscapeDataString(settings.ApiVersion)}/{Uri.EscapeDataString(phoneNumberId)}/messages");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.AccessToken);
        request.Content = JsonContent.Create(new
        {
            messaging_product = "whatsapp", recipient_type = "individual", to = recipient,
            type = "text", text = new { body = text },
        });
        using HttpResponseMessage response = await http.SendAsync(request, ct);
        if ((int)response.StatusCode >= 500) throw new HttpRequestException("WhatsApp send outcome is uncertain.");
        if (!response.IsSuccessStatusCode) throw new WhatsAppRejectedException();
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!json.RootElement.TryGetProperty("messages", out JsonElement messages) || messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() == 0
            || !messages[0].TryGetProperty("id", out JsonElement id) || id.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(id.GetString()))
            throw new JsonException("WhatsApp returned no message ID.");
        return id.GetString()!;
    }
}
