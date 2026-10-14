namespace Parrot.Application.Agents;

/// <summary>
/// A voice conversation with the AI about the business: a signed ElevenLabs link the browser
/// connects to, valid for <see cref="ExpiresIn"/> seconds, and what ElevenLabs passes back to
/// the AI so it knows whose conversation it is.
/// </summary>
public sealed record OrientationVoiceSession(
    string SignedUrl,
    IReadOnlyDictionary<string, string> DynamicVariables,
    string FirstMessage,
    int ExpiresIn);

/// <summary>The orientation service, called for the signed-in owner with a service token minted for them.</summary>
public interface IOrientationClient
{
    Task<OrientationVoiceSession> StartVoiceSessionAsync(string ownerUserId, CancellationToken cancellationToken = default);
}
