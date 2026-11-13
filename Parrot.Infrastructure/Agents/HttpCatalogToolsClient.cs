using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Application.Agents;
using Parrot.Application.Auth;

namespace Parrot.Infrastructure.Agents;

/// <summary>
/// Calls the catalog's AI tools with a five-minute token holding only <c>catalog:ai-tools</c>
/// for the workspace, so it can read published products and prices and change nothing.
/// </summary>
public sealed class HttpCatalogToolsClient(HttpClient http, IServiceTokenFactory tokens) : ICatalogToolsClient
{
    // The catalog speaks snake_case and rejects fields it doesn't know.
    private static readonly JsonSerializerOptions JsonOptions = new ()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<JsonElement> CallAsync(string workspaceId, string tool, object arguments, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        if (http.BaseAddress is null)
        {
            throw new CatalogToolException("The catalog isn't configured, so products and prices can't be looked up.", refused: false);
        }

        using HttpRequestMessage request = new (HttpMethod.Post, $"api/catalog/ai/tools/{Uri.EscapeDataString(tool)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.MintForCatalogAiTools(workspaceId));
        request.Content = JsonContent.Create(arguments, options: JsonOptions);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException)
        {
            throw new CatalogToolException("The catalog can't be reached right now.", refused: false);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CatalogToolException("The catalog took too long to answer.", refused: false);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);
            }

            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity)
            {
                throw new CatalogToolException(await MessageAsync(response, ct) ?? "The catalog refused the request.", refused: true);
            }

            throw new CatalogToolException($"The catalog answered {(int)response.StatusCode}.", refused: false);
        }
    }

    /// <summary>The catalog's own explanation, from its error body's <c>message</c>.</summary>
    private static async Task<string?> MessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);
            return body.ValueKind == JsonValueKind.Object && body.TryGetProperty("message", out JsonElement message) && message.ValueKind == JsonValueKind.String
                ? message.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
