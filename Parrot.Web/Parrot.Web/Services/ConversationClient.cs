using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Parrot.Application.Conversations;
using Parrot.Web.Features.Auth.State;

namespace Parrot.Web.Services;

public sealed class ConversationClient(HttpClient http, AuthState auth)
{
    public Task<List<ConversationSummary>> ListAsync(int skip, CancellationToken ct) =>
        RequestAsync<List<ConversationSummary>>(HttpMethod.Get, $"api/conversations?skip={skip}&take=50", null, ct);
    public Task<MessagePage> ReadAsync(Guid connection, string sender, string? before, CancellationToken ct) =>
        RequestAsync<MessagePage>(HttpMethod.Get, Path(connection, sender) + (before is null ? "" : $"&before={Uri.EscapeDataString(before)}"), null, ct);
    public Task<ConversationMessage> SendAsync(Guid connection, string sender, SendReplyRequest request, CancellationToken ct) =>
        RequestAsync<ConversationMessage>(HttpMethod.Post, Path(connection, sender), request, ct);
    private static string Path(Guid connection, string sender) => $"api/conversations/{connection}/messages?sender={Uri.EscapeDataString(sender)}";

    private async Task<T> RequestAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var token = auth.IsAuthenticated ? auth.CurrentUser!.Token : throw new InvalidOperationException("Sign in to load messages.");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
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
