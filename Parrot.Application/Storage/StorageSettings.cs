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

    /// <summary>Largest image the upload endpoint accepts.</summary>
    public int MaxImageBytes { get; init; } = 5 * 1024 * 1024;
}
