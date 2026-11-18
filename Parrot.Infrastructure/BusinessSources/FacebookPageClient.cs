using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Parrot.Application.BusinessSources;
using Parrot.Application.Integrations;

namespace Parrot.Infrastructure.BusinessSources;

public sealed class FacebookPageClient : IFacebookPageClient
{
    public const string HttpClientName = "FacebookProfile";

    private readonly HttpClient _http;
    private readonly string _apiVersion;
    private readonly ILogger<FacebookPageClient> _logger;

    public FacebookPageClient(
        IHttpClientFactory httpClientFactory,
        IOptions<FacebookSettings> settings,
        ILogger<FacebookPageClient> logger)
    {
        _http = httpClientFactory.CreateClient(HttpClientName);
        _apiVersion = settings.Value.ApiVersion;
        _logger = logger;
    }

    public async Task<FacebookPageData?> GetPageAsync(string pageId, string accessToken, CancellationToken ct)
    {
        string url = $"/{_apiVersion}/{pageId}?fields=name,about,description,website,category,fan_count";
        _logger.LogDebug("facebook.page.fetch_start PageId={PageId} ApiVersion={ApiVersion}", pageId, _apiVersion);
        try
        {
            (PageResponse? response, MetaGraph.Refusal? refused) = await MetaGraph.GetAsync<PageResponse>(_http, url, accessToken, ct);
            if (refused is not null)
            {
                MetaGraph.LogRefused(_logger, "facebook.page", pageId, _apiVersion, refused);
                return null;
            }
            if (response is null)
            {
                _logger.LogWarning("facebook.page.fetch_empty PageId={PageId}", pageId);
                return null;
            }
            _logger.LogInformation("facebook.page.fetch_ok PageId={PageId} Name={Name} Category={Category}",
                pageId, response.Name, response.Category);
            return new FacebookPageData(
                response.Name,
                response.About,
                response.Description,
                response.Website,
                response.Category,
                response.FanCount);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "facebook.page.fetch_failed PageId={PageId} ApiVersion={ApiVersion}",
                pageId, _apiVersion);
            return null;
        }
    }

    private sealed class PageResponse
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("about")] public string? About { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("website")] public string? Website { get; set; }
        [JsonPropertyName("category")] public string? Category { get; set; }
        [JsonPropertyName("fan_count")] public long? FanCount { get; set; }
    }
}
