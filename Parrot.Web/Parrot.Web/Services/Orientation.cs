using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.SharedComponents.Services;

namespace Parrot.Web.Services;

/// <summary>The AI service's HttpClient for responses read as they arrive: no timeout, each read is cancelled by its caller.</summary>
public sealed class OmniAiStreams(HttpClient http)
{
    public HttpClient Http => http;
}

/// <summary>A form the AI sent during orientation. EventId acknowledges it; ReviewId is what it reviews.</summary>
public sealed record OrientationForm(string EventId, string ReviewId, JsonElement Form);

/// <summary>
/// Teaching one agent about the business (/api/v1/orientation, on the AI service, as the signed-in
/// owner): a live voice call, a typed or recorded turn whose answer streams back, and the forms the
/// AI sends meanwhile. Answering a form only tells the service it was delivered: it does not save
/// the answers or approve anything, so the screens must not say so.
/// </summary>
public sealed class OrientationApi(OmniAiClient omni, OmniAiStreams streams)
{
    // The voice session comes back in camelCase (signedUrl), unlike the setup routes.
    private static readonly JsonSerializerOptions VoiceJson = new(JsonSerializerDefaults.Web);

    /// <summary>The signed ElevenLabs link for a voice call about this agent's job.</summary>
    public async Task<AiVoiceSession> StartVoiceAsync(string agentId, CancellationToken cancellationToken = default)
    {
        using var request = omni.Request(HttpMethod.Post, $"api/v1/orientation/voice/session/{Uri.EscapeDataString(agentId)}");
        using var response = await omni.Http.SendAsync(request, cancellationToken);
        await response.EnsureApiSuccessAsync();
        return await response.Content.ReadFromJsonAsync<AiVoiceSession>(VoiceJson, cancellationToken)
            ?? throw new HttpRequestException("The AI service returned an empty response.");
    }

    /// <summary>The AI's answer to what the owner typed, piece by piece as it is written. Join the pieces as they are.</summary>
    public IAsyncEnumerable<string> SendTextAsync(string agentId, string text, CancellationToken cancellationToken = default)
    {
        var body = new MultipartFormDataContent { { new StringContent(text), "text" } };
        return StreamTextAsync(agentId, body, cancellationToken);
    }

    /// <summary>The AI's answer to a recording (audio/webm, audio/mp4…): the service transcribes it and answers in text.</summary>
    public IAsyncEnumerable<string> SendAudioAsync(string agentId, Stream audio, string contentType, string fileName,
        CancellationToken cancellationToken = default)
    {
        var file = new StreamContent(audio);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        var body = new MultipartFormDataContent { { file, "file", fileName } };
        return StreamTextAsync(agentId, body, cancellationToken);
    }

    /// <summary>
    /// The forms the AI sends for this agent, as they arrive, until cancelled or the stream ends (the
    /// token expired, the connection dropped): the caller reconnects. The same form can arrive again
    /// until it is answered; keep them by EventId.
    /// </summary>
    public async IAsyncEnumerable<OrientationForm> ReadFormsAsync(string agentId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var request = omni.Request(HttpMethod.Get, $"api/v1/orientation/forms/stream/{Uri.EscapeDataString(agentId)}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await streams.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await response.EnsureApiSuccessAsync();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var expected = $"orientation.form.{agentId}";
        string? id = null, name = null;
        var data = new List<string>();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                // A blank line ends an event; comments (": keep-alive") never start one.
                if (data.Count > 0 && name == expected && !string.IsNullOrWhiteSpace(id)
                    && Parse(id, string.Join("\n", data)) is { } form)
                    yield return form;
                id = name = null;
                data.Clear();
                continue;
            }
            if (line.StartsWith(':')) continue;
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' ')) value = value[1..];
            switch (field)
            {
                case "id": id = value; break;
                case "event": name = value; break;
                case "data": data.Add(value); break;
            }
        }
    }

    /// <summary>
    /// Tells the service this form was answered, so it stops sending it. The answers go with it but
    /// are not saved yet: success means delivered, not saved or approved.
    /// </summary>
    public Task AnswerFormAsync(string agentId, OrientationForm form, IReadOnlyDictionary<string, object?> answers,
        CancellationToken cancellationToken = default) =>
        omni.SendAsync<bool>(HttpMethod.Post, $"api/v1/orientation/forms/answer/{Uri.EscapeDataString(agentId)}",
            new FormAnswer(form.EventId, form.ReviewId, JsonSerializer.Serialize(answers)), cancellationToken: cancellationToken);

    private async IAsyncEnumerable<string> StreamTextAsync(string agentId, MultipartFormDataContent body,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var content = body;
        using var request = omni.Request(HttpMethod.Post, $"api/v1/orientation/text/{Uri.EscapeDataString(agentId)}");
        request.Content = content;
        using var response = await streams.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await response.EnsureApiSuccessAsync();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var buffer = new char[512];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            yield return new string(buffer, 0, count);
    }

    // The data is { review_id, form }, form being the form's own JSON as a string. One that does not
    // read is skipped rather than ending the stream.
    private static OrientationForm? Parse(string eventId, string json)
    {
        try
        {
            var data = JsonSerializer.Deserialize<FormData>(json);
            if (data is null || string.IsNullOrWhiteSpace(data.ReviewId) || string.IsNullOrWhiteSpace(data.Form)) return null;
            using var form = JsonDocument.Parse(data.Form);
            return new OrientationForm(eventId, data.ReviewId, form.RootElement.Clone());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record FormData(
        [property: JsonPropertyName("review_id")] string ReviewId,
        [property: JsonPropertyName("form")] string Form);

    private sealed record FormAnswer(string EventId, string ReviewId, string Answer);
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

    // What the business types about itself has nowhere to go here yet: "Tell us about your
    // business" and "Add to business knowledge" stay hidden (CanSaveIntro).
    public bool CanSaveIntro => false;

    public Task SaveIntroAsync(BusinessIntro intro) => throw new NotSupportedException();

    public Task<string?> StartingPointAsync() => Task.FromResult<string?>(null);
}
