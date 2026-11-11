using Microsoft.JSInterop;

namespace Parrot.Web.Services;

public enum FeedbackSound
{
    Error,
    Send,
    Incoming,
}

/// <summary>Applying a sound choice to this session and persisting it are separate browser outcomes.</summary>
public sealed record FeedbackPreferenceSaveResult(bool Applied, bool Saved, bool Enabled);

// The C# face of js/feedback.js. Sound and haptics are for messages only: a
// reply sent (Send), a reply that failed (Error), a customer writing in
// (Incoming). Fire-and-forget: a sound must never hold up or break a flow.
public sealed class FeedbackService(IJSRuntime js)
{
    public void Play(FeedbackSound sound) => _ = PlayAsync(sound);

    public async Task PlayAsync(FeedbackSound sound)
    {
        try
        {
            await js.InvokeVoidAsync("parrotFeedback.play", sound.ToString().ToLowerInvariant());
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or TaskCanceledException)
        {
            // Prerendering, a dropped circuit, or the script not loaded yet.
        }
    }

    public async Task<bool> IsEnabledAsync()
        => await ReadEnabledAsync() ?? true;

    /// <summary>Null means the browser could not be read; a settings UI must not invent a successful read.</summary>
    public async Task<bool?> ReadEnabledAsync()
    {
        try
        {
            return await js.InvokeAsync<bool>("parrotFeedback.isEnabled");
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or TaskCanceledException)
        {
            return null;
        }
    }

    public async Task SetEnabledAsync(bool enabled)
        => _ = await TrySetEnabledAsync(enabled);

    /// <summary>Only Saved confirms persistence. A blocked localStorage may still allow muting this session.</summary>
    public async Task<FeedbackPreferenceSaveResult> TrySetEnabledAsync(bool enabled)
    {
        try
        {
            return await js.InvokeAsync<FeedbackPreferenceSaveResult?>("parrotFeedback.setEnabled", enabled)
                ?? new(false, false, enabled);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or TaskCanceledException)
        {
            return new(false, false, enabled);
        }
    }
}
