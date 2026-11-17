using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Application.Auth;
using Parrot.Application.Payments;

namespace Parrot.Infrastructure.Payments;

/// <summary>
/// The catalog's stock holds (api/catalog/orders/holds), called with a five-minute
/// catalog:orders token for the order's workspace.
/// </summary>
public sealed class HttpCatalogStockClient(HttpClient http, IServiceTokenFactory tokens) : ICatalogStockClient
{
    private static readonly JsonSerializerOptions Json = new ()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task HoldAsync(string workspaceId, string reference, DateTime expiresAt, IReadOnlyList<StockLine> lines, CancellationToken ct)
    {
        using HttpResponseMessage response = await SendAsync(workspaceId, "api/catalog/orders/holds", new { Reference = reference, ExpiresAt = expiresAt, Lines = lines }, ct);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new StockShortException(await MessageAsync(response, ct) ?? "Some of the items aren't in stock any more.");
        }

        await EnsureAsync(response, ct);
    }

    public async Task<IReadOnlyList<StockLine>> CommitAsync(string workspaceId, string reference, CancellationToken ct)
    {
        using HttpResponseMessage response = await SendAsync(workspaceId, $"api/catalog/orders/holds/{Uri.EscapeDataString(reference)}/commit", null, ct);
        await EnsureAsync(response, ct);
        CommitAnswer? answer = await response.Content.ReadFromJsonAsync<CommitAnswer>(Json, ct);
        return answer?.Short?.Select(s => new StockLine(s.ProductId, s.VariantId, s.Quantity)).ToArray() ?? [];
    }

    public async Task ReleaseAsync(string workspaceId, string reference, CancellationToken ct)
    {
        using HttpResponseMessage response = await SendAsync(workspaceId, $"api/catalog/orders/holds/{Uri.EscapeDataString(reference)}/release", null, ct);
        await EnsureAsync(response, ct);
    }

    private async Task<HttpResponseMessage> SendAsync(string workspaceId, string path, object? body, CancellationToken ct)
    {
        if (http.BaseAddress is null)
        {
            throw new CatalogStockUnavailableException("The catalog isn't configured.");
        }

        using HttpRequestMessage request = new (HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.MintForCatalogOrders(workspaceId));
        request.Content = body is null ? null : JsonContent.Create(body, options: Json);
        try
        {
            return await http.SendAsync(request, ct);
        }
        catch (HttpRequestException)
        {
            throw new CatalogStockUnavailableException("The catalog can't be reached right now.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CatalogStockUnavailableException("The catalog took too long to answer.");
        }
    }

    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new CatalogStockUnavailableException($"The catalog answered {(int)response.StatusCode}: {await MessageAsync(response, ct)}");
        }
    }

    private static async Task<string?> MessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
            return body.ValueKind == JsonValueKind.Object && body.TryGetProperty("message", out JsonElement message) ? message.GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private sealed record CommitAnswer(List<ShortAnswer>? Short);

    private sealed record ShortAnswer(Guid ProductId, Guid? VariantId, int Quantity, int Left);
}
