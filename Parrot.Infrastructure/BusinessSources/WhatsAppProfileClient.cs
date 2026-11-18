using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Parrot.Application.BusinessSources;
using Parrot.Application.Integrations;
using Parrot.Application.Logging;

namespace Parrot.Infrastructure.BusinessSources;

public sealed class WhatsAppProfileClient : IWhatsAppProfileClient
{
    public const string HttpClientName = "WhatsAppProfile";

    private readonly HttpClient _http;
    private readonly string _apiVersion;
    private readonly ILogger<WhatsAppProfileClient> _logger;

    public WhatsAppProfileClient(IHttpClientFactory httpClientFactory, IOptions<WhatsAppSettings> settings, ILogger<WhatsAppProfileClient> logger)
    {
        _http = httpClientFactory.CreateClient(HttpClientName);
        _apiVersion = settings.Value.ApiVersion;
        _logger = logger;
    }

    public async Task<WhatsAppProfileData?> GetProfileAsync(string phoneNumberId, string accessToken, CancellationToken ct)
    {
        _logger.LogDebug("whatsapp.profile.fetch_start PhoneNumberId={PhoneNumberId} ApiVersion={ApiVersion}",
            phoneNumberId, _apiVersion);
        try
        {
            (PhoneNumberResponse? phone, MetaGraph.Refusal? phoneRefused) = await MetaGraph.GetAsync<PhoneNumberResponse>(
                _http, $"/{_apiVersion}/{phoneNumberId}?fields=verified_name,display_phone_number", accessToken, ct);
            if (phoneRefused is not null)
            {
                MetaGraph.LogRefused(_logger, "whatsapp.phone_number", phoneNumberId, _apiVersion, phoneRefused);
                return null;
            }

            (BusinessProfileResponse? profileResponse, MetaGraph.Refusal? profileRefused) = await MetaGraph.GetAsync<BusinessProfileResponse>(
                _http, $"/{_apiVersion}/{phoneNumberId}/whatsapp_business_profile?fields=about,description,address,email,vertical,profile_picture_url,websites",
                accessToken, ct);
            if (profileRefused is not null)
            {
                MetaGraph.LogRefused(_logger, "whatsapp.business_profile", phoneNumberId, _apiVersion, profileRefused);
                return null;
            }

            BusinessProfileEntry? profile = profileResponse?.Data?.FirstOrDefault();

            WhatsAppProfileData result = new WhatsAppProfileData(
                VerifiedName: phone?.VerifiedName,
                DisplayPhoneNumber: phone?.DisplayPhoneNumber,
                About: profile?.About,
                Description: profile?.Description,
                Address: profile?.Address,
                Email: profile?.Email,
                Vertical: profile?.Vertical,
                ProfilePictureUrl: profile?.ProfilePictureUrl,
                Websites: profile?.Websites ?? []);

            _logger.LogInformation(
                "whatsapp.profile.fetch_ok PhoneNumberId={PhoneNumberId} VerifiedName={VerifiedName} DisplayPhone={DisplayPhone}",
                phoneNumberId, result.VerifiedName, LogMask.Phone(result.DisplayPhoneNumber));
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "whatsapp.profile.fetch_failed PhoneNumberId={PhoneNumberId} ApiVersion={ApiVersion}",
                phoneNumberId, _apiVersion);
            return null;
        }
    }

    private sealed class PhoneNumberResponse
    {
        [JsonPropertyName("verified_name")] public string? VerifiedName { get; set; }
        [JsonPropertyName("display_phone_number")] public string? DisplayPhoneNumber { get; set; }
    }

    private sealed class BusinessProfileResponse
    {
        [JsonPropertyName("data")] public List<BusinessProfileEntry>? Data { get; set; }
    }

    private sealed class BusinessProfileEntry
    {
        [JsonPropertyName("about")] public string? About { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("address")] public string? Address { get; set; }
        [JsonPropertyName("email")] public string? Email { get; set; }
        [JsonPropertyName("vertical")] public string? Vertical { get; set; }
        [JsonPropertyName("profile_picture_url")] public string? ProfilePictureUrl { get; set; }
        [JsonPropertyName("websites")] public List<string>? Websites { get; set; }
    }
}
