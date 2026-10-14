namespace Parrot.Application.Storage;

/// <summary>
/// Durable storage that is never readable by URL: a customer's pictures and files. Unlike
/// <see cref="IObjectStorage"/> (profile images, served from a public bucket) nothing here hands out an
/// address; the API reads the bytes back for a signed-in teammate and streams them.
/// </summary>
public interface IPrivateObjectStorage
{
    Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default);

    /// <summary>The stored bytes, or null when there is no object under the key. The caller disposes the result.</summary>
    Task<StoredObject?> GetAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>An object read back from storage. Disposing it closes the stream.</summary>
public sealed class StoredObject(Stream content, string contentType, long length) : IDisposable
{
    public Stream Content { get; } = content;

    public string ContentType { get; } = contentType;

    public long Length { get; } = length;

    public void Dispose() => Content.Dispose();
}
