using Microsoft.JSInterop;

namespace Parrot.Web.Services;

public enum FeedbackSound
{
    Success,
    Error,
    Send,
    Incoming,
}

// The C# face of js/feedback.js, for the moments only the server knows about:
// a sign-in going through, a reply sent, a customer's message arriving.
// Clicks, switches, code digits, toasts and inline errors are picked up by the
// script itself. Fire-and-forget: a sound must never hold up or break a flow.
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
