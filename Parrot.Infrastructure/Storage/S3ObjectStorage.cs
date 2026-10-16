using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using Parrot.Application.Storage;

namespace Parrot.Infrastructure.Storage;

/// <summary>
/// Writes to an S3-compatible bucket. This is the only class in the solution
/// that names Amazon; everything above it sees <see cref="IObjectStorage"/>.
/// </summary>
public sealed class S3ObjectStorage : IObjectStorage
{
    private readonly Lazy<IAmazonS3> _lazyClient;
    private readonly StorageSettings _settings;

    public S3ObjectStorage(IOptions<StorageSettings> settings)
    {
        _settings = settings.Value;

        AmazonS3Config config = new AmazonS3Config();

        if (string.IsNullOrWhiteSpace(_settings.ServiceUrl))
        {
            config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(_settings.Region);
        }
        else
        {
            // An S3-compatible service rather than Amazon's own endpoints:
            // address it path-style and say which region to sign for.
            config.ServiceURL = _settings.ServiceUrl;
            config.ForcePathStyle = true;
            config.AuthenticationRegion = _settings.Region;
        }

        // Built on first use, so a deployment with no credentials yet still boots.
        _lazyClient = new Lazy<IAmazonS3>(() => new AmazonS3Client(_settings.AccessKey, _settings.SecretKey, config));
    }

    public async Task<string> PutAsync(
        string key,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        await _lazyClient.Value.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = _settings.Bucket,
                Key = key,
                InputStream = content,
                ContentType = contentType,

                // The caller owns the stream and disposes it once the upload
                // is done.
                AutoCloseStream = false,
            },
            cancellationToken);

        return $"{_settings.PublicBaseUrl.TrimEnd('/')}/{key.TrimStart('/')}";
    }
}
