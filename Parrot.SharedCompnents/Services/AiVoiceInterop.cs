using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Parrot.SharedComponents.Services;

/// <summary>A voice conversation's signed ElevenLabs link, from the server, and what to pass to the AI with it.</summary>
public sealed record AiVoiceSession(string SignedUrl, IReadOnlyDictionary<string, string> DynamicVariables, string FirstMessage);

/// <summary>
/// A live voice conversation with the AI (ai-voice.js): ElevenLabs hears the person and speaks
/// the AI's answers. Unlike <see cref="VoiceInterop"/>, the AI decides what is said; the page is
/// told what each side said and who is talking.
/// </summary>
public sealed class AiVoiceInterop(IJSRuntime js) : IAsyncDisposable
{
    private readonly Lazy<Task<IJSObjectReference>> _module = new(() =>
        js.InvokeAsync<IJSObjectReference>("import", "./_content/Parrot.SharedComponents/js/ai-voice.js").AsTask());

    /// <summary>
    /// Connects and starts the conversation. On the target: <paramref name="message"/>(who, text) with who
    /// "user" or "agent"; <paramref name="mode"/>("speaking" or "listening"); <paramref name="ended"/>(reason)
    /// when it ends other than through <see cref="AiConversation.EndAsync"/>; <paramref name="failed"/>(message);
    /// <paramref name="alive"/>() every few seconds, returning whether the call should still be on (false, or no
    /// answer because the page has lost the server, and the browser ends it by itself).
    /// The level of whoever is talking goes on <paramref name="elements"/> as --level.
    /// </summary>
    public async Task<AiConversation> ConverseAsync<T>(AiVoiceSession session, IReadOnlyList<ElementReference> elements,
        DotNetObjectReference<T> target, string message, string mode, string ended, string failed, string alive) where T : class
    {
        var module = await _module.Value;
        var reference = await module.InvokeAsync<IJSObjectReference>("converse", session, elements, target,
            new { message, mode, ended, failed, alive });
        return new AiConversation(reference);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_module.IsValueCreated) return;
        try { await (await _module.Value).DisposeAsync(); }
        catch (JSDisconnectedException) { }
    }
}

/// <summary>A voice conversation in progress.</summary>
public sealed class AiConversation(IJSObjectReference reference) : IAsyncDisposable
{
    private bool _ended;

    /// <summary>Muted, the AI hears nothing; it can still finish what it is saying.</summary>
    public async Task SetMutedAsync(bool muted)
    {
        if (_ended) return;
        try { await reference.InvokeVoidAsync("mute", muted); }
        catch (JSDisconnectedException) { }
        catch (JSException) { }
    }

    public async Task EndAsync()
    {
        if (_ended) return;
        _ended = true;
        try
        {
            await reference.InvokeVoidAsync("end");
            await reference.DisposeAsync();
        }
        catch (JSDisconnectedException) { }
        catch (JSException) { }
    }

    public ValueTask DisposeAsync() => new(EndAsync());
}
