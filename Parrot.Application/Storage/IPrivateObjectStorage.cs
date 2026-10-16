namespace Parrot.Application.Storage;

/// <summary>
/// Durable storage that is never readable by URL: a customer's pictures and files. Unlike
/// <see cref="IObjectStorage"/> (profile images, served from a public bucket) nothing here hands out an
/// address; the API reads the bytes back for a signed-in teammate and streams them.
/// </summary>
public interface IPrivateObjectStorage
{
    /// <summary>
    /// Stores the content under key. The stream need not be seekable or have a known length: a download can be
    /// passed straight through, and memory use stays at a few megabytes whatever the size. If the stream throws
    /// part way, nothing is left stored under the key.
    /// </summary>
    Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default);

    /// <summary>The type and size of the object, or null when there is none. Reads no content.</summary>
    Task<StoredObjectInfo?> StatAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// The bytes from <paramref name="start"/> to <paramref name="end"/> (both inclusive, within the object's size),
    /// or null when there is no object. <see cref="StoredObject.Length"/> is the number of bytes in the range.
    /// </summary>
    Task<StoredObject?> GetRangeAsync(string key, long start, long end, CancellationToken cancellationToken = default);

    /// <summary>The stored bytes, or null when there is no object under the key. The caller disposes the result.</summary>
    Task<StoredObject?> GetAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>What storage knows about an object without reading it.</summary>
public sealed record StoredObjectInfo(string ContentType, long Length);

/// <summary>An object read back from storage. Disposing it closes the stream.</summary>
public sealed class StoredObject(Stream content, string contentType, long length) : IDisposable
{
    public Stream Content { get; } = content;

    public string ContentType { get; } = contentType;

    public long Length { get; } = length;

    public void Dispose() => Content.Dispose();
}
