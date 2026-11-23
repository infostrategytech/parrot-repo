namespace Parrot.Application.Storage;

public class StorageSettings
{
    public const string SectionName = "Storage";

    /// <summary>Which implementation Program.cs wires up, e.g. S3.</summary>
    public string Provider { get; init; } = "S3";

    /// <summary>Region the bucket lives in.</summary>
    public string Region { get; init; } = "us-east-1";

    public string Bucket { get; init; } = string.Empty;

    public string AccessKey { get; init; } = string.Empty;

    public string SecretKey { get; init; } = string.Empty;

    /// <summary>
    /// Endpoint requests are sent to. Left empty when the bucket is on the
    /// vendor's own endpoints; set it to point at an S3-compatible service.
    /// </summary>
    public string ServiceUrl { get; init; } = string.Empty;

    /// <summary>
    /// The prefix a browser reads objects from, bucket included — e.g.
    /// https://parrot.s3.eu-west-1.amazonaws.com. Separate from ServiceUrl
    /// because the two are deliberately different hosts: the server writes to
    /// a private endpoint, the browser reads from a public one.
    /// </summary>
    public string PublicBaseUrl { get; init; } = string.Empty;

    /// <summary>
    /// The bucket customers' pictures and files are copied into. It must NOT allow anonymous reads (unlike
    /// <see cref="Bucket"/>): the API is the only reader.
    /// </summary>
    public string MediaBucket { get; init; } = "parrot-private";

    /// <summary>
    /// The API's public address, e.g. https://api.example.com. The download links in MessagePersisted start with it.
    /// Empty: those links are left out (url is null) and everything else about the message is unchanged.
    /// </summary>
    public string MediaLinkBaseUrl { get; init; } = string.Empty;

    /// <summary>
    /// True: the message is stored at once with its files pending and a job per file goes to the media topic.
    /// False (the default): the worker copies the files before it stores the message. Turn it on only when
    /// the media workers run and the inbox refreshes a thread when a file finishes.
    /// </summary>
    public bool DeferInboundMedia { get; init; }

    /// <summary>Largest file the message worker saves from a customer. A bigger one is kept as a note that it was sent.</summary>
    public long MaxInboundMediaBytes { get; init; } = 25 * 1024 * 1024;

    /// <summary>Largest image the upload endpoint accepts.</summary>
    public int MaxImageBytes { get; init; } = 5 * 1024 * 1024;
}
