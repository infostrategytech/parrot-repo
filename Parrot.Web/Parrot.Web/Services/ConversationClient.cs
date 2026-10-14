using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Parrot.Web.Features.Auth.State;

namespace Parrot.Web.Services;

// Reads into the web app's own InboxContracts, a superset of the API's
// conversation records: the API's fields plus the optional ones the inbox
// design uses. Today's API leaves those out, so they read as null.
public sealed class ConversationClient(HttpClient http, AuthState auth)
{
    public Task<List<InboxConversationDto>> ListAsync(int skip, CancellationToken ct) =>
        RequestAsync<List<InboxConversationDto>>(HttpMethod.Get, $"api/conversations?skip={skip}&take=50", null, ct);
    public Task<InboxMessagePageDto> ReadAsync(Guid connection, string sender, string? before, CancellationToken ct) =>
        RequestAsync<InboxMessagePageDto>(HttpMethod.Get, Path(connection, sender) + (before is null ? "" : $"&before={Uri.EscapeDataString(before)}"), null, ct);
    public Task<InboxMessageDto> SendAsync(Guid connection, string sender, InboxReplyDto request, CancellationToken ct) =>
        RequestAsync<InboxMessageDto>(HttpMethod.Post, Path(connection, sender), request, ct);
    public Task<InboxUnreadDto> UnreadAsync(CancellationToken ct) =>
        RequestAsync<InboxUnreadDto>(HttpMethod.Get, "api/conversations/unread", null, ct);
    public Task<InboxMarkReadResultDto> MarkReadAsync(Guid connection, string sender, string upToMessageId, CancellationToken ct) =>
        RequestAsync<InboxMarkReadResultDto>(HttpMethod.Post,
            $"api/conversations/{connection}/read?sender={Uri.EscapeDataString(sender)}", new InboxMarkReadDto(upToMessageId), ct);
    public async Task SetPinnedAsync(Guid connection, string sender, bool pinned, CancellationToken ct) =>
        await RequestAsync<InboxPinDto>(HttpMethod.Put, $"api/conversations/{connection}/pin?sender={Uri.EscapeDataString(sender)}", new InboxPinDto(pinned), ct);
    /// <summary>Returns the inbox total, which counts a conversation marked unread.</summary>
    public Task<InboxUnreadDto> SetMarkedUnreadAsync(Guid connection, string sender, bool markedUnread, CancellationToken ct) =>
        RequestAsync<InboxUnreadDto>(HttpMethod.Put,
            $"api/conversations/{connection}/marked-unread?sender={Uri.EscapeDataString(sender)}", new InboxMarkedUnreadDto(markedUnread), ct);
    private static string Path(Guid connection, string sender) => $"api/conversations/{connection}/messages?sender={Uri.EscapeDataString(sender)}";
    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private async Task<T> RequestAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var token = auth.IsAuthenticated ? auth.CurrentUser!.Token : throw new InvalidOperationException("Sign in to load messages.");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        // Null optional fields are left out, so the API sees the body it expects.
        if (body is not null) request.Content = JsonContent.Create(body, options: WriteOptions);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var text = "Could not complete the request. Check your connection and try again.";
            try
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (json.RootElement.TryGetProperty("message", out var message)) text = message.GetString() ?? text;
            }
            catch (JsonException) { }
            throw new HttpRequestException(text, null, response.StatusCode);
        }
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct)
            ?? throw new HttpRequestException("The server returned an empty response.");
    }
}
