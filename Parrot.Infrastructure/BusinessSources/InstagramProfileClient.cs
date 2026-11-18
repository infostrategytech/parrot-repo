using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Parrot.Application.BusinessSources;
using Parrot.Application.Integrations;

namespace Parrot.Infrastructure.BusinessSources;

public sealed class InstagramProfileClient : IInstagramProfileClient
{
    public const string HttpClientName = "InstagramProfile";

    private readonly HttpClient _http;
    private readonly string _apiVersion;
    private readonly ILogger<InstagramProfileClient> _logger;

    public InstagramProfileClient(
        IHttpClientFactory httpClientFactory,
        IOptions<InstagramSettings> settings,
        ILogger<InstagramProfileClient> logger)
    {
        _http = httpClientFactory.CreateClient(HttpClientName);
        _apiVersion = settings.Value.ApiVersion;
        _logger = logger;
    }

    public async Task<InstagramProfileData?> GetProfileAsync(string igUserId, string accessToken, CancellationToken ct)
    {
        string url = $"/{_apiVersion}/{igUserId}?fields=name,biography,website,followers_count,profile_picture_url";
        _logger.LogDebug("instagram.profile.fetch_start IgUserId={IgUserId} ApiVersion={ApiVersion}", igUserId, _apiVersion);
        try
        {
            (ProfileResponse? response, MetaGraph.Refusal? refused) = await MetaGraph.GetAsync<ProfileResponse>(_http, url, accessToken, ct);
            if (refused is not null)
            {
                MetaGraph.LogRefused(_logger, "instagram.profile", igUserId, _apiVersion, refused);
                return null;
            }
            if (response is null)
            {
                _logger.LogWarning("instagram.profile.fetch_empty IgUserId={IgUserId}", igUserId);
                return null;
            }
            _logger.LogInformation("instagram.profile.fetch_ok IgUserId={IgUserId} Name={Name} FollowerCount={FollowerCount}",
                igUserId, response.Name, response.FollowersCount);
            return new InstagramProfileData(
                response.Name,
                response.Biography,
                response.Website,
                response.FollowersCount,
                response.ProfilePictureUrl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "instagram.profile.fetch_failed IgUserId={IgUserId} ApiVersion={ApiVersion}",
                igUserId, _apiVersion);
            return null;
        }
    }

    private sealed class ProfileResponse
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("biography")] public string? Biography { get; set; }
        [JsonPropertyName("website")] public string? Website { get; set; }
        [JsonPropertyName("followers_count")] public long? FollowersCount { get; set; }
        [JsonPropertyName("profile_picture_url")] public string? ProfilePictureUrl { get; set; }
    }
}
