using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    private readonly ILogger<S3ItemPhotoStorage> _log;

    public S3ItemPhotoStorage(IOptions<CatalogOptions> options, ILogger<S3ItemPhotoStorage>? log = null)
    {
        _options = options.Value.Photos;
        _log = log ?? NullLogger<S3ItemPhotoStorage>.Instance;
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

        LogSettings();
    }

    /// <summary>
    /// Says once, at start, where photos go and how their links are built; a link base that isn't a whole
    /// address (https:///media, from an empty domain) makes every photo a broken link, so it is called out.
    /// </summary>
    private void LogSettings()
    {
        _log.LogInformation(
            "Item photos: Bucket={Bucket} Region={Region} Endpoint={Endpoint} PublicBaseUrl={PublicBaseUrl} Credentials={Credentials}",
            string.IsNullOrWhiteSpace(_options.Bucket) ? "(none)" : _options.Bucket,
            _options.Region,
            string.IsNullOrWhiteSpace(_options.ServiceUrl) ? "Amazon S3" : _options.ServiceUrl,
            string.IsNullOrWhiteSpace(_options.PublicBaseUrl) ? "(none)" : _options.PublicBaseUrl,
            string.IsNullOrEmpty(_options.AccessKey) ? "default chain" : "access key");

        if (!IsConfigured)
        {
            _log.LogWarning("Item photos can't be uploaded: Catalog:Photos:Bucket and Catalog:Photos:PublicBaseUrl must both be set.");
        }
        else if (!Uri.TryCreate(_options.PublicBaseUrl, UriKind.Absolute, out Uri? publicBase)
            || publicBase.Scheme is not ("http" or "https")
            || string.IsNullOrEmpty(publicBase.Host)
            || (!publicBase.Host.Contains('.') && publicBase.Host != "localhost"))
        {
            _log.LogWarning(
                "Catalog:Photos:PublicBaseUrl {PublicBaseUrl} isn't a whole web address, so photo links will be broken. Expected the bucket's address, such as https://<bucket>.s3.<region>.amazonaws.com.",
                _options.PublicBaseUrl);
        }
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.Bucket) && !string.IsNullOrWhiteSpace(_options.PublicBaseUrl);

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken ct)
    {
        try
        {
            PutObjectResponse response = await _client.Value.PutObjectAsync(
                new PutObjectRequest
                {
                    BucketName = _options.Bucket,
                    Key = key,
                    InputStream = content,
                    ContentType = contentType,
                    AutoCloseStream = false,
                },
                ct);
            _log.LogDebug("Stored photo in S3. Bucket={Bucket} Key={Key} Status={Status} ETag={ETag}", _options.Bucket, key, (int)response.HttpStatusCode, response.ETag);
        }
        catch (AmazonS3Exception ex)
        {
            // S3's own words: AccessDenied (the key's policy), NoSuchBucket (the name or region), and so on.
            _log.LogError(
                ex,
                "S3 refused the photo. Bucket={Bucket} Key={Key} Status={Status} Code={Code} RequestId={RequestId}",
                _options.Bucket, key, (int)ex.StatusCode, ex.ErrorCode, ex.RequestId);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "The photo couldn't be sent to storage. Bucket={Bucket} Key={Key} Endpoint={Endpoint}",
                _options.Bucket, key, string.IsNullOrWhiteSpace(_options.ServiceUrl) ? "Amazon S3" : _options.ServiceUrl);
            throw;
        }
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
