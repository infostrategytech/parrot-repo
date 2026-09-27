using Microsoft.JSInterop;

namespace Parrot.Web.Services;

public enum FeedbackSound
{
    Error,
    Send,
    Incoming,
}

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
    {
        try
        {
            return await js.InvokeAsync<bool>("parrotFeedback.isEnabled");
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or TaskCanceledException)
        {
            return true;
        }
    }

    public async Task SetEnabledAsync(bool enabled)
    {
        try
        {
            await js.InvokeVoidAsync("parrotFeedback.setEnabled", enabled);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or TaskCanceledException)
        {
        }
    }
}
