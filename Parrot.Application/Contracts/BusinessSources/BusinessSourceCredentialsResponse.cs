namespace Parrot.Application.Contracts.BusinessSources;

/// <summary>
/// The credentials Omni AI needs to call the external platform API for a business source.
/// </summary>
public sealed record BusinessSourceCredentialsResponse(
    /// <summary>Decrypted access token for the platform's API (Meta Graph API for all current channels).</summary>
    string AccessToken,
    /// <summary>Graph API version the caller should use, e.g. "v25.0".</summary>
    string ApiVersion);
