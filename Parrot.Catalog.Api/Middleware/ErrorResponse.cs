using System.ComponentModel;

namespace Parrot.Catalog.Api.Middleware;

/// <summary>The same envelope Parrot.Api uses, so clients branch on one shape.</summary>
[Description("The envelope every failed request comes back in.")]
public sealed record ErrorResponse
{
    public bool Success => false;

    public required string ErrorCode { get; init; }

    public required string Message { get; init; }

    public IEnumerable<string>? Errors { get; init; }

    public string? TraceId { get; init; }
}
