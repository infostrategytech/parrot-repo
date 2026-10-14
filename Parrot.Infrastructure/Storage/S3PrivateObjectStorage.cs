using Amazon.S3;
using Amazon.S3.Model;
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
    private readonly IAmazonS3 _client;
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

        _client = new AmazonS3Client(value.AccessKey, value.SecretKey, config);
    }

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default) =>
        await _client.PutObjectAsync(
            new PutObjectRequest { BucketName = _bucket, Key = key, InputStream = content, ContentType = contentType, AutoCloseStream = false },
            cancellationToken);

    public async Task<StoredObject?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            GetObjectResponse response = await _client.GetObjectAsync(_bucket, key, cancellationToken);
            // The stream owns the response: disposing the stream releases the connection.
            return new StoredObject(response.ResponseStream, response.Headers.ContentType ?? "application/octet-stream", response.ContentLength);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }
}
