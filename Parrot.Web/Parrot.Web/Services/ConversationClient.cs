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
    public async Task<InboxMessagePageDto> ReadAsync(Guid connection, string sender, string? before, CancellationToken ct)
    {
        var page = await RequestAsync<InboxMessagePageDto>(HttpMethod.Get, Path(connection, sender) + (before is null ? "" : $"&before={Uri.EscapeDataString(before)}"), null, ct);
        return page with { Messages = [.. page.Messages.Select(m => m.Attachments is { Count: > 0 } files ? m with { Attachments = [.. files.Select(Resolve)] } : m)] };
    }
    /// <summary>Every file the customer sent, newest first. Pass the last page's NextBefore for the next one.</summary>
    public async Task<InboxMediaPageDto> MediaAsync(Guid connection, string sender, string? before, CancellationToken ct)
    {
        var page = await RequestAsync<InboxMediaPageDto>(HttpMethod.Get,
            $"api/conversations/{connection}/media?sender={Uri.EscapeDataString(sender)}" + (before is null ? "" : $"&before={Uri.EscapeDataString(before)}"), null, ct);
        return page with { Items = [.. page.Items.Select(i => i with { Media = Resolve(i.Media) })] };
    }
    // The API sends a file's address relative to itself; the browser needs the whole address, since the inbox is another site.
    private InboxMediaDto Resolve(InboxMediaDto media) =>
        media.Url is { } url && !media.IsLink && http.BaseAddress is { } api && !Uri.IsWellFormedUriString(url, UriKind.Absolute)
            ? media with { Url = new Uri(api, url).ToString() }
            : media;
    public Task<InboxMessageDto> SendAsync(Guid connection, string sender, InboxReplyDto request, CancellationToken ct) =>
        RequestAsync<InboxMessageDto>(HttpMethod.Post, Path(connection, sender), request, ct);
    /// <summary>Sends a picture or document, with the typed text as its caption. The same request id makes a retry safe.</summary>
    public async Task<InboxMessageDto> SendFileAsync(Guid connection, string sender, Guid requestId, string? caption, InboxPendingFile file, CancellationToken ct)
    {
        var form = new MultipartFormDataContent { { new StringContent(requestId.ToString()), "RequestId" } };
        if (!string.IsNullOrWhiteSpace(caption)) form.Add(new StringContent(caption), "Caption");
        form.Add(new ByteArrayContent(file.Bytes), "File", file.Name);
        using var response = await SendAsync(HttpMethod.Post,
            $"api/conversations/{connection}/attachments?sender={Uri.EscapeDataString(sender)}", form, ct);
        var message = await response.Content.ReadFromJsonAsync<InboxMessageDto>(cancellationToken: ct)
            ?? throw new HttpRequestException("The server returned an empty response.");
        return message.Attachments is { Count: > 0 } files ? message with { Attachments = [.. files.Select(Resolve)] } : message;
    }
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
    public Task<List<InboxNoteDto>> NotesAsync(Guid connection, string sender, CancellationToken ct) =>
        RequestAsync<List<InboxNoteDto>>(HttpMethod.Get, NotesPath(connection, sender), null, ct);
    public Task<InboxNoteDto> AddNoteAsync(Guid connection, string sender, string text, CancellationToken ct) =>
        RequestAsync<InboxNoteDto>(HttpMethod.Post, NotesPath(connection, sender), new InboxAddNoteDto(text), ct);
    public async Task DeleteNoteAsync(Guid connection, string sender, string noteId, CancellationToken ct) =>
        await SendAsync(HttpMethod.Delete, $"api/conversations/{connection}/notes/{Uri.EscapeDataString(noteId)}?sender={Uri.EscapeDataString(sender)}", null, ct);
    private static string NotesPath(Guid connection, string sender) => $"api/conversations/{connection}/notes?sender={Uri.EscapeDataString(sender)}";
    private static string Path(Guid connection, string sender) => $"api/conversations/{connection}/messages?sender={Uri.EscapeDataString(sender)}";
    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private async Task<T> RequestAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var response = await SendAsync(method, path, body, ct);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct)
            ?? throw new HttpRequestException("The server returned an empty response.");
    }

    // The response, once it is known to have succeeded; a failure becomes an HttpRequestException carrying the API's message.
    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct) =>
        SendAsync(method, path, body is null ? null : JsonContent.Create(body, options: WriteOptions), ct);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        var token = auth.IsAuthenticated ? auth.CurrentUser!.Token : throw new InvalidOperationException("Sign in to load messages.");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        // Null optional fields are left out, so the API sees the body it expects.
        request.Content = content;
        var response = await http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode) return response;
        using (response)
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
    }
}
