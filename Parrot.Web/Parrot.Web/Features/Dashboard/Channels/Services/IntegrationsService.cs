using System.Net.Http.Headers;
using System.Net.Http.Json;
using Parrot.Web.Features.Dashboard.Channels.Models;

namespace Parrot.Web.Features.Dashboard.Channels.Services;

public class IntegrationsService : IIntegrationsService
{
    private readonly HttpClient _httpClient;

    public IntegrationsService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public Task SaveWhatsAppAccessTokenAsync(Guid id, string accessToken, string token, CancellationToken cancellationToken = default) =>
        SendCommandAsync(HttpMethod.Put, $"api/whatsapp/connections/{id}/access-token", token,
            new { AccessToken = accessToken }, cancellationToken);

    public Task SaveWhatsAppAppSecretAsync(Guid id, string appSecret, string token, CancellationToken cancellationToken = default) =>
        SendCommandAsync(HttpMethod.Put, $"api/whatsapp/connections/{id}/app-secret", token,
            new { AppSecret = appSecret }, cancellationToken);

    public Task SaveFacebookAppSecretAsync(string appSecret, string token, CancellationToken cancellationToken = default) =>
        SendCommandAsync(HttpMethod.Put, "api/facebook/setup/app-secret", token,
            new { AppSecret = appSecret }, cancellationToken);

    public Task<FacebookWebhookSetup> GetFacebookSetupAsync(string token, CancellationToken cancellationToken = default) =>
        SendAsync<FacebookWebhookSetup>(HttpMethod.Get, "api/facebook/setup", token, null, cancellationToken);

    public Task SaveFacebookPageAsync(string pageId, string pageName, string accessToken, string token,
        CancellationToken cancellationToken = default) =>
        SendCommandAsync(HttpMethod.Put, "api/facebook/setup/page", token,
            new { PageId = pageId, PageName = pageName, AccessToken = accessToken }, cancellationToken);

    public Task ConfigureWhatsAppAccountAsync(Guid id, string businessAccountId, string phoneNumberId, string token, CancellationToken cancellationToken = default) =>
        SendCommandAsync(HttpMethod.Put, $"api/whatsapp/connections/{id}/account", token,
            new { WhatsAppBusinessAccountId = businessAccountId, PhoneNumberId = phoneNumberId }, cancellationToken);

    public Task DisableWhatsAppAsync(Guid id, string token, CancellationToken cancellationToken = default) =>
        SendCommandAsync(HttpMethod.Post, $"api/whatsapp/connections/{id}/disable", token, null, cancellationToken);

    private async Task SendCommandAsync(HttpMethod method, string path, string token, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public Task<List<WhatsAppConnectionInfo>> GetWhatsAppConnectionsAsync(string token, CancellationToken cancellationToken = default) =>
        SendAsync<List<WhatsAppConnectionInfo>>(HttpMethod.Get, "api/whatsapp/connections", token, null, cancellationToken);

    public Task<WhatsAppSetup> CreateWhatsAppSetupAsync(string name, string token, CancellationToken cancellationToken = default) =>
        SendAsync<WhatsAppSetup>(HttpMethod.Post, "api/whatsapp/connections", token, new { Name = name }, cancellationToken);

    public Task<WhatsAppSetup> GetWhatsAppSetupAsync(Guid id, string token, CancellationToken cancellationToken = default) =>
        SendAsync<WhatsAppSetup>(HttpMethod.Get, $"api/whatsapp/connections/{id}/setup", token, null, cancellationToken);

    public Task<WhatsAppConnectionInfo> GetWhatsAppStatusAsync(Guid id, string token, CancellationToken cancellationToken = default) =>
        SendAsync<WhatsAppConnectionInfo>(HttpMethod.Get, $"api/whatsapp/connections/{id}", token, null, cancellationToken);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, string token, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
            ?? throw new HttpRequestException("The server returned an empty response.");
    }

    public async Task<string?> GetWebhookUrlAsync(string platform, string token, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/integrations/business-webhook/{platform}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;
        WebhookUrlResponse? result = await response.Content.ReadFromJsonAsync<WebhookUrlResponse>(cancellationToken);
        return result?.WebhookUrl;
    }
}
