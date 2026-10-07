using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Parrot.SharedComponents.Services;

/// <summary>What this browser can do with voice.</summary>
public sealed record VoiceCapabilities(bool Speak, bool Listen, bool Microphone, bool Record);

/// <summary>A finished voice note: what was said, and how long it was.</summary>
/// <summary>A recorded voice note: what was said, how long, the audio (a URL in this browser only) and its waveform (0 to 1 per bar).</summary>
public sealed record VoiceNote(string Transcript, int Seconds, string? Url = null, IReadOnlyList<double>? Peaks = null);

/// <summary>
/// The page's side of voice: the agent speaking, the person replying, voice
/// notes, and the live level a mascot glows with. Backed by the browser's own
/// speech today (voice.js); ElevenLabs replaces it later behind these calls.
/// </summary>
public sealed class VoiceInterop(IJSRuntime js) : IAsyncDisposable
{
    private readonly Lazy<Task<IJSObjectReference>> _module = new(() =>
        js.InvokeAsync<IJSObjectReference>("import", "./_content/Parrot.SharedComponents/js/voice.js").AsTask());

    public async Task<VoiceCapabilities> CapabilitiesAsync() =>
        await (await _module.Value).InvokeAsync<VoiceCapabilities>("capabilities");

    /// <summary>The microphone's level on <paramref name="element"/> until disposed. Throws if the person refuses the microphone.</summary>
    public async Task<IAsyncDisposable> MicLevelAsync(ElementReference element) =>
        new Handle(await (await _module.Value).InvokeAsync<IJSObjectReference>("micLevel", element), "release");

    /// <summary>The microphone's level on several elements at once (the orb and the light round the surface).</summary>
    public async Task<IAsyncDisposable> MicLevelAsync(IReadOnlyList<ElementReference> elements) =>
        new Handle(await (await _module.Value).InvokeAsync<IJSObjectReference>("micLevel", elements), "release");

    /// <summary>
    /// Says <paramref name="text"/> aloud and calls <paramref name="done"/> on the target when finished.
    /// As each word is spoken the level on <paramref name="element"/> pulses, and inside
    /// <paramref name="captions"/> every element with data-at (a character position in the text)
    /// gets data-on, so a transcript can type out in step with the voice.
    /// </summary>
    public async Task<VoiceSpeech> SpeakAsync<T>(ElementReference element, string text, DotNetObjectReference<T> target, string done, ElementReference? captions = null, string? stalled = null) where T : class =>
        new(await (await _module.Value).InvokeAsync<IJSObjectReference>("speak", element, text, target, done, captions, stalled));

    /// <summary>As above, with the voice's level on several elements at once.</summary>
    /// <remarks>stalled: called instead of done if the voice has not started within a few seconds (a poor connection).</remarks>
    public async Task<VoiceSpeech> SpeakAsync<T>(IReadOnlyList<ElementReference> elements, string text, DotNetObjectReference<T> target, string done, ElementReference? captions = null, string? stalled = null) where T : class =>
        new(await (await _module.Value).InvokeAsync<IJSObjectReference>("speak", elements, text, target, done, captions, stalled));

    /// <summary>The connection, for voice: "offline", "poor" or "ok".</summary>
    public async Task<string> NetworkStateAsync() =>
        await (await _module.Value).InvokeAsync<string>("networkState");

    /// <summary>Tells target[changed](state) when the connection changes between offline, poor and ok.</summary>
    public async Task<IAsyncDisposable> WatchNetworkAsync<T>(DotNetObjectReference<T> target, string changed) where T : class =>
        new Handle(await (await _module.Value).InvokeAsync<IJSObjectReference>("watchNetwork", target, changed), "dispose");

    /// <summary>Listens for one reply, calling the named methods on the target as words arrive, when it ends, or if it fails.</summary>
    public async Task<IAsyncDisposable> ListenAsync<T>(DotNetObjectReference<T> target, string interim, string final, string failed) where T : class =>
        new Handle(await (await _module.Value).InvokeAsync<IJSObjectReference>("listen", target, interim, final, failed), "abort");

    /// <summary>Starts a voice note, its level on <paramref name="element"/>. Throws if the person refuses the microphone.</summary>
    /// <summary>Plays a voice note through audio; host shows the progress (--progress); target[changed](playing) on play, pause and end.</summary>
    public async Task<VoicePlayer> BindPlayerAsync<T>(ElementReference audio, ElementReference host, int seconds, DotNetObjectReference<T> target, string changed) where T : class =>
        new(await (await _module.Value).InvokeAsync<IJSObjectReference>("bindPlayer", audio, host, seconds, target, changed));

    public async Task<VoiceRecording> RecordNoteAsync(ElementReference element) =>
        new(await (await _module.Value).InvokeAsync<IJSObjectReference>("recordNote", element));

    /// <summary>The site's microphone permission: granted, prompt, denied, or unknown.</summary>
    public async Task<string> MicPermissionAsync() =>
        await (await _module.Value).InvokeAsync<string>("micPermission");

    /// <summary>Calls the named method on the target with the new state whenever the permission changes.</summary>
    public async Task<IAsyncDisposable> WatchMicPermissionAsync<T>(DotNetObjectReference<T> target, string changed) where T : class =>
        new Handle(await (await _module.Value).InvokeAsync<IJSObjectReference>("watchMicPermission", target, changed), "dispose");

    /// <summary>
    /// Asks for the microphone, showing the browser's prompt if needed, and releases it:
    /// granted, denied (person or site), system (the computer blocks the browser),
    /// none (no microphone), busy (another app has it) or error.
    /// </summary>
    public async Task<string> RequestMicAsync() =>
        await (await _module.Value).InvokeAsync<string>("requestMic");

    /// <summary>Calls the named method with true or false as the tab is shown or hidden.</summary>
    public async Task<IAsyncDisposable> WatchVisibilityAsync<T>(DotNetObjectReference<T> target, string changed) where T : class =>
        new Handle(await (await _module.Value).InvokeAsync<IJSObjectReference>("watchVisibility", target, changed), "dispose");

    /// <summary>Ctrl+D (⌘D) calls the named method, as Google Meet's mute does, until disposed.</summary>
    public async Task<IAsyncDisposable> BindMuteShortcutAsync<T>(DotNetObjectReference<T> target, string pressed) where T : class =>
        new Handle(await (await _module.Value).InvokeAsync<IJSObjectReference>("bindMuteShortcut", target, pressed), "dispose");

    public async ValueTask DisposeAsync()
    {
        if (!_module.IsValueCreated) return;
        try { await (await _module.Value).DisposeAsync(); }
        catch (JSDisconnectedException) { }
    }

    private sealed class Handle(IJSObjectReference reference, string stop) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await reference.InvokeVoidAsync(stop);
                await reference.DisposeAsync();
            }
            catch (JSDisconnectedException) { }
            catch (JSException) { }
        }
    }
}

/// <summary>Speech in progress: how far it has got, and a way to stop it.</summary>
public sealed class VoiceSpeech(IJSObjectReference reference) : IAsyncDisposable
{
    private bool _done;

    /// <summary>The character position in the text the voice has reached.</summary>
    public async Task<int> SaidAsync()
    {
        if (_done) return 0;
        try { return await reference.InvokeAsync<int>("said"); }
        catch (JSDisconnectedException) { return 0; }
        catch (JSException) { return 0; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_done) return;
        _done = true;
        try
        {
            await reference.InvokeVoidAsync("cancel");
            await reference.DisposeAsync();
        }
        catch (JSDisconnectedException) { }
        catch (JSException) { }
    }
}

/// <summary>A voice note being recorded: stop it to keep it, or cancel it.</summary>
/// <summary>A voice note's playback: play or pause; disposing stops it.</summary>
public sealed class VoicePlayer(IJSObjectReference reference) : IAsyncDisposable
{
    public async Task ToggleAsync()
    {
        try { await reference.InvokeVoidAsync("toggle"); }
        catch (JSDisconnectedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await reference.InvokeVoidAsync("dispose");
            await reference.DisposeAsync();
        }
        catch (JSDisconnectedException) { }
        catch (JSException) { }
    }
}

public sealed class VoiceRecording(IJSObjectReference reference) : IAsyncDisposable
{
    private bool _done;

    public async Task<VoiceNote> StopAsync()
    {
        _done = true;
        var note = await reference.InvokeAsync<VoiceNote>("stop");
        await reference.DisposeAsync();
        return note;
    }

    public async Task CancelAsync()
    {
        if (_done) return;
        _done = true;
        try
        {
            await reference.InvokeVoidAsync("cancel");
            await reference.DisposeAsync();
        }
        catch (JSDisconnectedException) { }
        catch (JSException) { }
    }

    public async ValueTask DisposeAsync() => await CancelAsync();
}
