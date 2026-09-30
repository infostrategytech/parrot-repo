namespace Parrot.Application.Storage;

public interface IImageService
{
    /// <summary>
    /// Stores an image a signed-in user uploaded and returns the URL it can be
    /// read from. The caller saves that URL on the profile separately.
    /// </summary>
    Task<string> SaveUserImageAsync(
        string userId,
        Stream content,
        string contentType,
        long length,
        CancellationToken cancellationToken = default);
}
