namespace Parrot.Application.Storage;

/// <summary>
/// The only place that writes bytes somewhere durable. Nothing above the
/// Infrastructure layer knows which vendor sits behind it, so changing where
/// uploads live means swapping this implementation in Program.cs and leaving
/// everything else untouched.
/// </summary>
public interface IObjectStorage
{
    /// <summary>Stores the content under key and returns the URL it can be read from.</summary>
    Task<string> PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default);
}
