using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Web.Features.Auth.State;

namespace Parrot.Web.Services;

/// <summary>
/// The AI service (Omni-AI) as the signed-in person: agents, their channels and orientation, under
/// /api/v1. It reads the user, workspace and permissions from the session token itself, so nothing
/// here sends them. Failures are ApiException, with the service's errorCode and message.
/// </summary>
/// <remarks>
/// There is no SessionExpiryHandler on this client: a 401 here can mean the service does not share
/// Parrot's signing key, and that must not sign the owner out of Parrot.
/// </remarks>
public sealed class OmniAiClient(HttpClient http, AuthState auth)
{
    /// <summary>The service's own names: agent_id, use_case, is_live.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public HttpClient Http => http;

    /// <summary>The session's token, for a request built by the caller (a stream).</summary>
    public string Token => auth.IsAuthenticated ? auth.CurrentUser!.Token : throw new InvalidOperationException("Sign in to continue.");

    public Task<T> GetAsync<T>(string path, CancellationToken cancellationToken = default) =>
        SendAsync<T>(HttpMethod.Get, path, null, cancellationToken: cancellationToken);

    /// <param name="version">Sent as If-Match: the version this change was made against.</param>
    /// <param name="idempotencyKey">One per intended create, reused only to retry that same create.</param>
    public async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, int? version = null,
        string? idempotencyKey = null, CancellationToken cancellationToken = default)
    {
        using var request = Request(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        if (version is { } v) request.Headers.TryAddWithoutValidation("If-Match", v.ToString(CultureInfo.InvariantCulture));
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        using var response = await http.SendAsync(request, cancellationToken);
        await response.EnsureApiSuccessAsync();
        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken)
            ?? throw new HttpRequestException("The AI service returned an empty response.");
    }

    /// <summary>Every page of a cursor list ({ items, next_cursor }).</summary>
    public async Task<List<T>> ListAllAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        var all = new List<T>();
        var separator = path.Contains('?') ? '&' : '?';
        string? cursor = null;
        do
        {
            var page = await GetAsync<Page<T>>(
                cursor is null ? $"{path}{separator}limit=100" : $"{path}{separator}limit=100&cursor={Uri.EscapeDataString(cursor)}",
                cancellationToken);
            all.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        return all;
    }

    /// <summary>A request carrying the session's token, for callers that read the response as a stream.</summary>
    public HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return request;
    }

    private sealed record Page<T>(List<T> Items, string? NextCursor);
}
