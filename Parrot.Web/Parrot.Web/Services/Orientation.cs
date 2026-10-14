using System.Net.Http.Headers;
using System.Net.Http.Json;
using Parrot.SharedComponents.Services;
using Parrot.Web.Features.Auth.State;

namespace Parrot.Web.Services;

/// <summary>
/// Parrot's /api/orientation for the signed-in owner: the signed ElevenLabs link for talking to
/// the AI about the business. Failures are the API's ApiException, with its message.
/// </summary>
public sealed class OrientationApi(HttpClient http, AuthState auth)
{
    public async Task<AiVoiceSession> StartVoiceAsync(CancellationToken cancellationToken = default)
    {
        var token = auth.IsAuthenticated ? auth.CurrentUser!.Token : throw new InvalidOperationException("Sign in to talk to the AI.");
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/orientation/voice/session");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, cancellationToken);
        await response.EnsureApiSuccessAsync();
        return await response.Content.ReadFromJsonAsync<AiVoiceSession>(cancellationToken)
            ?? throw new HttpRequestException("The server returned an empty response.");
    }
}

/// <summary>
/// With voice orientation switched on (OmniAi:OrientationVoice), "Teach your AI" opens (from
/// "Teach more") so the owner can talk to the AI about the business. The orientation service
/// keeps what it learns and has nothing to approve yet, so nothing is held back on the way to
/// creating an agent.
/// </summary>
public sealed class OrientationBusinessKnowledge : IBusinessKnowledge
{
    public bool Available => true;

    public Task<bool> HasApprovedAsync() => Task.FromResult(true);

    public Task<IReadOnlyList<KnowledgeCard>> GetDraftAsync() => Task.FromResult<IReadOnlyList<KnowledgeCard>>([]);

    public Task ApproveAsync(IReadOnlyList<KnowledgeCard> cards) => Task.CompletedTask;
}
