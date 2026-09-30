using System.ComponentModel;

namespace Parrot.Api.Middleware;

/// <summary>Every non-2xx body the API produces, and the only one it produces.</summary>
[Description("The envelope every failed request comes back in.")]
public record ErrorResponse
{
    [Description("Always false on this envelope.")]
    public bool Success => false;

    [Description("The value to branch on; stable between releases.")]
    public required string ErrorCode { get; init; }

    [Description("The sentence a person is meant to read; also kept stable.")]
    public required string Message { get; init; }

    [Description("The individual reasons when one request broke several rules at once.")]
    public IEnumerable<string>? Errors { get; init; }

    [Description("The id to quote when reporting the failure.")]
    public string? TraceId { get; init; }
}
