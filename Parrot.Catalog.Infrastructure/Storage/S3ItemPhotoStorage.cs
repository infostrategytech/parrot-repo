using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using Parrot.Catalog.Application.Services;

namespace Parrot.Catalog.Infrastructure.Storage;

/// <summary>Items' photos in an S3-compatible bucket. The only class in the catalog that names Amazon.</summary>
public sealed class S3ItemPhotoStorage : IItemPhotoStorage
{
    // DeleteObjects takes at most this many keys per call.
    private const int DeleteBatch = 1000;

    private readonly Lazy<IAmazonS3> _client;
    private readonly CatalogPhotoOptions _options;

    public S3ItemPhotoStorage(IOptions<CatalogOptions> options)
    {
        _options = options.Value.Photos;
        AmazonS3Config config = new AmazonS3Config();
        if (string.IsNullOrWhiteSpace(_options.ServiceUrl))
        {
            config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(_options.Region);
        }
        else
        {
            // An S3-compatible service (MinIO locally): path-style, signed for the region given.
            config.ServiceURL = _options.ServiceUrl;
            config.ForcePathStyle = true;
            config.AuthenticationRegion = _options.Region;
        }

        // Built on first use, so the service boots before the bucket's credentials are set.
        _client = new Lazy<IAmazonS3>(() => string.IsNullOrEmpty(_options.AccessKey)
            ? new AmazonS3Client(config)
            : new AmazonS3Client(_options.AccessKey, _options.SecretKey, config));
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.Bucket) && !string.IsNullOrWhiteSpace(_options.PublicBaseUrl);

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken ct)
    {
        await _client.Value.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = _options.Bucket,
                Key = key,
                InputStream = content,
                ContentType = contentType,
                AutoCloseStream = false,
            },
            ct);
    }

    public async Task DeleteAsync(IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        foreach (string[] batch in keys.Distinct(StringComparer.Ordinal).Chunk(DeleteBatch))
        {
            await _client.Value.DeleteObjectsAsync(
                new DeleteObjectsRequest
                {
                    BucketName = _options.Bucket,
                    Objects = batch.Select(key => new KeyVersion { Key = key }).ToList(),
                    Quiet = true,
                },
                ct);
        }
    }

    public string UrlFor(string key) => $"{_options.PublicBaseUrl.TrimEnd('/')}/{key.TrimStart('/')}";
}
