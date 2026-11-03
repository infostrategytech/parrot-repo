namespace Parrot.Catalog.Application.Services;

/// <summary>
/// Where items' photos are kept (an S3 bucket). Keys are the catalog's own media keys,
/// prefixed by the tenant; nothing above this knows the vendor.
/// </summary>
public interface IItemPhotoStorage
{
    /// <summary>False when no bucket is set: photos can't be added, and the rest of an item still saves.</summary>
    bool IsConfigured { get; }

    Task PutAsync(string key, Stream content, string contentType, CancellationToken ct);

    /// <summary>Removes the files. One already gone is not an error.</summary>
    Task DeleteAsync(IReadOnlyCollection<string> keys, CancellationToken ct);

    /// <summary>Where the browser reads the photo from.</summary>
    string UrlFor(string key);
}
