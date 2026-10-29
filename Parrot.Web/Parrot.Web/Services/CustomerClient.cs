using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Parrot.Web.Features.Auth.State;

namespace Parrot.Web.Services;

public sealed class CustomerClient(HttpClient http, AuthState auth)
{
    public Task<CustomerPageDto> ListAsync(
        string? search, string? stage, string? channel, int skip, int take, CancellationToken ct)
    {
        string path = $"api/customers?skip={skip}&take={take}";
        if (!string.IsNullOrWhiteSpace(search)) path += $"&search={Uri.EscapeDataString(search)}";
        if (!string.IsNullOrWhiteSpace(stage)) path += $"&stage={Uri.EscapeDataString(stage)}";
        if (!string.IsNullOrWhiteSpace(channel)) path += $"&channel={Uri.EscapeDataString(channel)}";
        return RequestAsync<CustomerPageDto>(HttpMethod.Get, path, null, ct);
    }

    public Task<CustomerDetailDto> GetAsync(Guid id, CancellationToken ct) =>
        RequestAsync<CustomerDetailDto>(HttpMethod.Get, $"api/customers/{id}", null, ct);

    public Task<CustomerDetailDto> CreateAsync(CreateCustomerDto request, CancellationToken ct) =>
        RequestAsync<CustomerDetailDto>(HttpMethod.Post, "api/customers", request, ct);

    public Task<CustomerDetailDto> UpdateStageAsync(Guid id, string stage, CancellationToken ct) =>
        RequestAsync<CustomerDetailDto>(HttpMethod.Put, $"api/customers/{id}/stage", new UpdateStageDto(stage), ct);

    public Task<CustomerDetailDto> UpdateTagsAsync(Guid id, List<string> tags, CancellationToken ct) =>
        RequestAsync<CustomerDetailDto>(HttpMethod.Put, $"api/customers/{id}/tags", new UpdateTagsDto(tags), ct);

    public Task<CustomerDetailDto> UpdateChannelIdentifierAsync(Guid id, string? channelPlatform, string? channelIdentifier, CancellationToken ct) =>
        RequestAsync<CustomerDetailDto>(HttpMethod.Put, $"api/customers/{id}/channel", new UpdateChannelIdentifierDto(channelPlatform, channelIdentifier), ct);

    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    internal async Task<T> RequestAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        string token = auth.IsAuthenticated ? auth.CurrentUser!.Token : throw new InvalidOperationException("Sign in to continue.");
        using HttpRequestMessage request = new(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body, options: WriteOptions);
        using HttpResponseMessage response = await http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct)
                   ?? throw new HttpRequestException("The server returned an empty response.");
        string text = "Could not complete the request. Check your connection and try again.";
        try
        {
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (json.RootElement.TryGetProperty("message", out JsonElement message)) text = message.GetString() ?? text;
        }
        catch (JsonException) { }
        throw new HttpRequestException(text, null, response.StatusCode);
    }
}
