using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using Microsoft.Extensions.Options;
using Parrot.Application.Storage;

namespace Parrot.Infrastructure.Storage;

/// <summary>
/// Customers' pictures and files, in a bucket that allows no anonymous reads. Same endpoint and credentials
/// as <see cref="S3ObjectStorage"/>, a different bucket: the public bucket is opened for reads as a whole,
/// so a prefix in it could never be private.
/// </summary>
public sealed class S3PrivateObjectStorage : IPrivateObjectStorage
{
    private readonly Lazy<IAmazonS3> _lazyClient;
    private readonly string _bucket;

    public S3PrivateObjectStorage(IOptions<StorageSettings> settings)
    {
        StorageSettings value = settings.Value;
        _bucket = value.MediaBucket;

        AmazonS3Config config = new AmazonS3Config();
        if (string.IsNullOrWhiteSpace(value.ServiceUrl))
        {
            config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(value.Region);
        }
        else
        {
            config.ServiceURL = value.ServiceUrl;
            config.ForcePathStyle = true;
            config.AuthenticationRegion = value.Region;
        }

        // Built on first use, so a deployment with no credentials yet still boots.
        _lazyClient = new Lazy<IAmazonS3>(() => new AmazonS3Client(value.AccessKey, value.SecretKey, config));
    }

    // Below this a stream is read into memory and sent in one request; above it, in parts of this size.
    private const long PartSize = 5 * 1024 * 1024;

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        if (content.CanSeek)
        {
            await _lazyClient.Value.PutObjectAsync(
                new PutObjectRequest { BucketName = _bucket, Key = key, InputStream = content, ContentType = contentType, AutoCloseStream = false },
                cancellationToken);
            return;
        }

        // A stream of unknown length (a download in flight): sent in parts, so memory stays at one part at a
        // time. If the stream fails part way the upload is aborted and nothing is left under the key.
        using TransferUtility transfer = new(_lazyClient.Value, new TransferUtilityConfig { ConcurrentServiceRequests = 2 });
        await transfer.UploadAsync(
            new TransferUtilityUploadRequest
            {
                BucketName = _bucket, Key = key, InputStream = content, ContentType = contentType, AutoCloseStream = false, PartSize = PartSize,
            },
            cancellationToken);
    }

    public async Task<StoredObjectInfo?> StatAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            GetObjectMetadataResponse response = await _lazyClient.Value.GetObjectMetadataAsync(_bucket, key, cancellationToken);
            return new StoredObjectInfo(response.Headers.ContentType ?? "application/octet-stream", response.ContentLength);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<StoredObject?> GetRangeAsync(string key, long start, long end, CancellationToken cancellationToken = default)
    {
        try
        {
            GetObjectResponse response = await _lazyClient.Value.GetObjectAsync(
                new GetObjectRequest { BucketName = _bucket, Key = key, ByteRange = new ByteRange(start, end) }, cancellationToken);
            return new StoredObject(response.ResponseStream, response.Headers.ContentType ?? "application/octet-stream", response.ContentLength);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<StoredObject?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            GetObjectResponse response = await _lazyClient.Value.GetObjectAsync(_bucket, key, cancellationToken);
            // The stream owns the response: disposing the stream releases the connection.
            return new StoredObject(response.ResponseStream, response.Headers.ContentType ?? "application/octet-stream", response.ContentLength);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }
}
