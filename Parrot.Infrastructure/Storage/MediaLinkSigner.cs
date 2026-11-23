using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Parrot.Application.Messaging;
using Parrot.Application.Storage;

namespace Parrot.Infrastructure.Storage;

/// <summary>
/// Week-long download links for MessagePersisted, sealed the same way as the inbox's (Parrot.Api.Media.MediaLinks)
/// so the API's anonymous api/media endpoint serves both: the storage key and file name, protected with the shared
/// key ring. The message worker reads the API's key ring, so the links it makes open on the API.
/// A singleton on the message path, so it logs through ILogger like the other singletons there (IAppLogger is scoped).
/// </summary>
public sealed class MediaLinkSigner : IMediaLinkSigner
{
    /// <summary>The purpose both kinds of link are sealed with. Changing it breaks every link already handed out.</summary>
    public const string Purpose = "Parrot.CustomerMedia.v1";

    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    private readonly ITimeLimitedDataProtector _protector;
    private readonly ILogger<MediaLinkSigner> _logger;
    private readonly string? _baseUrl;

    public MediaLinkSigner(IDataProtectionProvider provider, IOptions<StorageSettings> settings, ILogger<MediaLinkSigner> logger)
    {
        _protector = provider.CreateProtector(Purpose).ToTimeLimitedDataProtector();
        _logger = logger;
        _baseUrl = NormalizeBaseUrl(settings.Value.MediaLinkBaseUrl);
        if (_baseUrl is null)
            logger.LogWarning(
                "media_links.disabled Setting={Setting}: not an http(s) address. Files in MessagePersisted go out without a url until it is set to the API's public address.",
                "Storage:MediaLinkBaseUrl");
    }

    public string? Sign(string storageKey, string fileName)
    {
        if (_baseUrl is null) return null;
        try
        {
            return $"{_baseUrl}/api/media/{_protector.Protect($"{storageKey}\n{fileName}", Lifetime)}";
        }
        catch (Exception ex)
        {
            // Safe to continue: the message still goes out, and the inbox reads its files through its own links.
            _logger.LogWarning(ex, "media_links.sign_failed: the file goes out without a url");
            return null;
        }
    }

    // An absolute http(s) address with no query, fragment or credentials, without its trailing slash; null otherwise.
    private static string? NormalizeBaseUrl(string configured)
    {
        string trimmed = configured.Trim().TrimEnd('/');
        return Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) && string.IsNullOrEmpty(uri.UserInfo)
            ? trimmed
            : null;
    }
}
