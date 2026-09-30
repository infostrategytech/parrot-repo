using Microsoft.Extensions.Options;
using Parrot.Domain.Exceptions;

namespace Parrot.Application.Storage;

public class ImageService : IImageService
{
    /// <summary>
    /// The content types the endpoint accepts, and the extension each is stored
    /// under. The file name the browser sent is never trusted — the key is
    /// built from this table alone.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ExtensionsByContentType =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["image/png"] = ".png",
            ["image/jpeg"] = ".jpg",
            ["image/gif"] = ".gif",
            ["image/webp"] = ".webp",
        };

    private readonly IObjectStorage _storage;
    private readonly StorageSettings _settings;

    public ImageService(IObjectStorage storage, IOptions<StorageSettings> settings)
    {
        _storage = storage;
        _settings = settings.Value;
    }

    public async Task<string> SaveUserImageAsync(
        string userId,
        Stream content,
        string contentType,
        long length,
        CancellationToken cancellationToken = default)
    {
        if (length <= 0)
        {
            throw new ValidationException("Choose an image to upload.");
        }

        if (length > _settings.MaxImageBytes)
        {
            throw new ValidationException($"Choose an image no larger than {_settings.MaxImageBytes / 1024} KB.");
        }

        if (!ExtensionsByContentType.TryGetValue(contentType, out string? extension))
        {
            throw new ValidationException("Choose a PNG, JPEG, GIF or WebP image.");
        }

        // Scoped to the account, so one person's upload can never overwrite
        // another's and so everything a person has uploaded can be found under
        // a single prefix. Keys are flat rather than paths, but the next
        // provider may be a filesystem, so the scope stays to a safe alphabet.
        string scope = new string(userId.Where(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (scope.Length == 0)
        {
            scope = Guid.NewGuid().ToString("N");
        }

        string key = $"users/{scope}/{Guid.NewGuid():N}{extension}";

        return await _storage.PutAsync(key, content, contentType, cancellationToken);
    }
}
