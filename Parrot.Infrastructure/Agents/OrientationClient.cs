using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Parrot.Application.Agents;
using Parrot.Application.Auth;
using Parrot.Application.Integrations;
using Parrot.Domain.Exceptions;

namespace Parrot.Infrastructure.Agents;

/// <summary>
/// The orientation service's voice session (POST api/v1/orientation/voice/session). It answers
/// FastAPI's way ({"detail": "..."}); its refusals are worded for people, so a 429 or 503 keeps
/// its words. The signed link is a credential and is never logged.
/// </summary>
public sealed class OrientationClient(
    HttpClient http,
    IServiceTokenFactory tokens,
    IOptions<OmniAiSettings> settings,
    ILogger<OrientationClient> logger) : IOrientationClient
{
    private const string Unavailable = "Voice chat with the AI could not be started. Try again in a moment.";

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    private static readonly string[] RequiredVariables = ["tenant_id", "secret__voice_session_token"];

    public async Task<OrientationVoiceSession> StartVoiceSessionAsync(string ownerUserId, CancellationToken cancellationToken = default)
    {
        if (!settings.Value.Enabled || http.BaseAddress is null)
        {
            throw new ServiceUnavailableException(
                "Voice chat with the AI is not switched on for this workspace.",
                new InvalidOperationException("OmniAi:Enabled is false or OmniAi:OrientationUrl is not an absolute URL."));
        }

        using HttpRequestMessage request = new(HttpMethod.Post, "api/v1/orientation/voice/session");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.Mint(ownerUserId));
        // A free ngrok tunnel answers a browser-looking request with its warning page instead.
        request.Headers.Add("ngrok-skip-browser-warning", "1");

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "orientation.unreachable");
            throw new ServiceUnavailableException(Unavailable, ex);
        }

        using (response)
        {
            string text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // A 401 means the orientation service did not accept Parrot's token: its JWT_SECRET,
                // JWT_ISSUER (ParrotApi) or JWT_AUDIENCE (ParrotApp) differ from Parrot's. Not the owner's to fix.
                logger.LogWarning("orientation.refused Status={Status}", (int)response.StatusCode);
                string? detail = DetailOf(text);
                int? wait = response.Headers.RetryAfter?.Delta is { } delta ? (int)Math.Ceiling(delta.TotalSeconds) : null;
                throw response.StatusCode switch
                {
                    HttpStatusCode.TooManyRequests => new ConflictException(
                        $"You've started several voice chats in a short time. Wait {(wait is > 0 ? $"{wait} seconds" : "a minute")}, then try again."),
                    HttpStatusCode.ServiceUnavailable when detail is not null => new ServiceUnavailableException(detail,
                        new HttpRequestException("Orientation answered 503.", null, response.StatusCode)),
                    _ => new ServiceUnavailableException(Unavailable,
                        new HttpRequestException($"Orientation answered {(int)response.StatusCode}.", null, response.StatusCode)),
                };
            }

            try
            {
                VoiceSessionWire? wire = JsonSerializer.Deserialize<VoiceSessionWire>(text, Wire);
                // Everything the call needs, before any audio starts: the link, the greeting, and both
                // values ElevenLabs passes back to the AI (whose conversation it is, and its credential).
                if (wire is not { SignedUrl.Length: > 0, FirstMessage.Length: > 0, DynamicVariables: { } variables }
                    || !Uri.TryCreate(wire.SignedUrl, UriKind.Absolute, out Uri? signed) || signed.Scheme != "wss"
                    || !RequiredVariables.All(name => variables.TryGetValue(name, out string? value) && value.Length > 0))
                {
                    throw new JsonException("The voice session is missing its wss signedUrl, firstMessage or dynamic variables.");
                }

                return new OrientationVoiceSession(wire.SignedUrl, variables, wire.FirstMessage, wire.ExpiresIn);
            }
            catch (JsonException ex)
            {
                logger.LogWarning("orientation.unreadable Reason={Reason}", ex.Message);
                throw new ServiceUnavailableException(Unavailable, ex);
            }
        }
    }

    private static string? DetailOf(string body)
    {
        try
        {
            using JsonDocument json = JsonDocument.Parse(body);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("detail", out JsonElement detail) && detail.ValueKind == JsonValueKind.String
                && detail.GetString() is { Length: > 0 } text
                ? text
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record VoiceSessionWire(string SignedUrl, Dictionary<string, string>? DynamicVariables, string? FirstMessage, int ExpiresIn);
}
