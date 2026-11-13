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

    public Task DisableFacebookAsync(string token, CancellationToken cancellationToken = default) =>
        SendCommandAsync(HttpMethod.Post, "api/facebook/setup/disable", token, null, cancellationToken);

    public Task<TikTokSetup> GetTikTokSetupAsync(string token, CancellationToken cancellationToken = default) =>
        SendAsync<TikTokSetup>(HttpMethod.Get, "api/tiktok/setup", token, null, cancellationToken);

    // TikTok checks the App ID and secret when the webhook is registered, and the API passes its reason on in a
    // 400. That reason is what the owner needs to fix a mistyped pair, so it is kept in the exception's message.
    public async Task SaveTikTokAppAsync(string appId, string appSecret, string token, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "api/tiktok/setup/app");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new { AppId = appId, AppSecret = appSecret });
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode) return;

        string? reason = null;
        if ((int)response.StatusCode is >= 400 and < 500)
        {
            try
            {
                using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                if (json.RootElement.TryGetProperty("message", out var message) && message.ValueKind == System.Text.Json.JsonValueKind.String)
                    reason = message.GetString();
            }
            catch (System.Text.Json.JsonException) { }
        }

        throw new HttpRequestException(reason ?? "The request failed.", null, response.StatusCode);
    }

    public Task<TikTokAuthorization> GetTikTokAuthorizationAsync(string token, CancellationToken cancellationToken = default) =>
        SendAsync<TikTokAuthorization>(HttpMethod.Get, "api/tiktok/setup/authorize", token, null, cancellationToken);

    public Task DisableTikTokAsync(string token, CancellationToken cancellationToken = default) =>
        SendCommandAsync(HttpMethod.Post, "api/tiktok/setup/disable", token, null, cancellationToken);

    public Task<InstagramWebhookSetup> GetInstagramSetupAsync(string token, CancellationToken cancellationToken = default) =>
        SendAsync<InstagramWebhookSetup>(HttpMethod.Get, "api/instagram/setup", token, null, cancellationToken);

    // Instagram's own reason (an expired token, a missing permission) comes back as the error's message.
    public Task SaveInstagramAccountAsync(string accessToken, string token, CancellationToken cancellationToken = default) =>
        SendCommandWithReasonAsync(HttpMethod.Put, "api/instagram/setup/account", token, new { AccessToken = accessToken }, cancellationToken);

    public Task SaveInstagramAppSecretAsync(string appSecret, string token, CancellationToken cancellationToken = default) =>
        SendCommandWithReasonAsync(HttpMethod.Put, "api/instagram/setup/app-secret", token, new { AppSecret = appSecret }, cancellationToken);

    public Task DisableInstagramAsync(string token, CancellationToken cancellationToken = default) =>
        SendCommandAsync(HttpMethod.Post, "api/instagram/setup/disable", token, null, cancellationToken);

    // Like SendCommandAsync, but a refusal carries the API's message, so the screen can say what to fix.
    private async Task SendCommandWithReasonAsync(HttpMethod method, string path, string token, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode) return;
        string? reason = null;
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (json.RootElement.TryGetProperty("message", out var message) && message.ValueKind == System.Text.Json.JsonValueKind.String)
                reason = message.GetString();
        }
        catch (System.Text.Json.JsonException) { }
        throw new HttpRequestException(reason, null, response.StatusCode);
    }

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

    public Task<CatalogueSyncResult> SyncCatalogueAsync(Guid connectionId, string token, CancellationToken cancellationToken = default) =>
        SendAsync<CatalogueSyncResult>(HttpMethod.Post, $"api/whatsapp/connections/{connectionId}/catalogue/sync", token, null, cancellationToken);

    public Task<CatalogueItemsResponse> GetCatalogueProductsAsync(Guid connectionId, int page, int pageSize, string token, string? cursor = null, CancellationToken cancellationToken = default)
    {
        string url = $"api/whatsapp/connections/{connectionId}/catalogue/products?page={page}&pageSize={pageSize}";
        if (cursor is not null)
            url += $"&cursor={Uri.EscapeDataString(cursor)}";
        return SendAsync<CatalogueItemsResponse>(HttpMethod.Get, url, token, null, cancellationToken);
    }

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

    public Task RefreshKnowledgeAsync(Guid connectionId, string token, CancellationToken cancellationToken = default) =>
        SendCommandAsync(HttpMethod.Post, $"api/whatsapp/connections/{connectionId}/refresh-knowledge", token, null, cancellationToken);

    public Task RefreshConnectionKnowledgeAsync(Guid connectionId, string token, CancellationToken cancellationToken = default) =>
        SendCommandAsync(HttpMethod.Post, $"api/connections/{connectionId}/refresh-knowledge", token, null, cancellationToken);

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
