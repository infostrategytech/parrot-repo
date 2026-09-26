using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Parrot.SharedComponents.Services;

/// <summary>
/// The typed face of wwwroot/js/aurora-interop.js. Components inject this
/// instead of IJSRuntime so that the set of things this app does in JavaScript
/// stays visible in one file rather than spreading as string literals.
/// </summary>
public sealed class AuroraInterop : IAsyncDisposable
{
    private readonly Lazy<Task<IJSObjectReference>> _module;

    public AuroraInterop(IJSRuntime js)
    {
        _module = new Lazy<Task<IJSObjectReference>>(() =>
            js.InvokeAsync<IJSObjectReference>("import", "./_content/Parrot.SharedComponents/js/aurora-interop.js").AsTask());
    }

    /// <summary>
    /// Holds focus inside <paramref name="container"/> until disposed, then
    /// returns it to wherever it was. Dispose it in the component's own
    /// DisposeAsync, not in an event handler.
    /// </summary>
    public async Task<IAsyncDisposable> TrapFocusAsync(ElementReference container)
    {
        var module = await _module.Value;
        var handle = await module.InvokeAsync<IJSObjectReference>("trapFocus", container);
        return new Handle(handle);
    }

    /// <summary>
    /// Calls back with whether the query matches, immediately and on change.
    /// </summary>
    public async Task<IAsyncDisposable> ObserveMediaAsync<T>(
        string query, DotNetObjectReference<T> reference, string method) where T : class
    {
        var module = await _module.Value;
        var handle = await module.InvokeAsync<IJSObjectReference>("observeMedia", query, reference, method);
        return new Handle(handle);
    }

    /// <summary>
    /// Opens a hidden file input. The browser only raises the picker for a
    /// genuine click on the input, so a styled button has to forward one.
    /// </summary>
    public async Task ClickAsync(ElementReference element)
    {
        var module = await _module.Value;
        await module.InvokeVoidAsync("clickElement", element);
    }

    /// <summary>
    /// Loads a classic script once, on first use, and waits for it. For heavy
    /// libraries a single screen needs, so they stay off every other page's
    /// first paint.
    /// </summary>
    public async Task EnsureScriptAsync(string url)
    {
        var module = await _module.Value;
        await module.InvokeVoidAsync("ensureScript", url);
    }

    /// <summary>Copies text to the clipboard. Silently does nothing if the browser refuses.</summary>
    public async Task CopyAsync(string value)
    {
        var module = await _module.Value;
        await module.InvokeVoidAsync("copyText", value);
    }

    public async Task ScrollToBottomAsync(ElementReference element)
    {
        var module = await _module.Value;
        await module.InvokeVoidAsync("scrollToBottom", element);
    }

    public async Task ScrollIntoViewAsync(ElementReference element)
    {
        var module = await _module.Value;
        await module.InvokeVoidAsync("scrollIntoView", element);
    }

    public async Task<string?> ReadStorageAsync(string key)
    {
        var module = await _module.Value;
        return await module.InvokeAsync<string?>("readStorage", key);
    }

    public async Task WriteStorageAsync(string key, string value)
    {
        var module = await _module.Value;
        await module.InvokeVoidAsync("writeStorage", key, value);
    }

    public async Task SetThemeAsync(string theme)
    {
        var module = await _module.Value;
        await module.InvokeVoidAsync("setTheme", theme);
    }

    public async Task<string> GetThemeAsync()
    {
        var module = await _module.Value;
        return await module.InvokeAsync<string>("getTheme");
    }

    public async ValueTask DisposeAsync()
    {
        if (!_module.IsValueCreated) return;
        try
        {
            var module = await _module.Value;
            await module.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
            // The page is going away; there is no module left to release.
        }
    }

    /// <summary>A JS-side subscription that has to be released from C#.</summary>
    private sealed class Handle(IJSObjectReference reference) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await reference.InvokeVoidAsync("release");
                await reference.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // Same as above: nothing to release on a page that has gone.
            }
        }
    }
}
