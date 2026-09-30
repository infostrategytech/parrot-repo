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
    /// Traps focus and calls <paramref name="escapeMethod"/> on
    /// <paramref name="escapeTarget"/> when Escape is pressed, and for no other key.
    /// </summary>
    public async Task<IAsyncDisposable> TrapFocusAsync<T>(ElementReference container, DotNetObjectReference<T> escapeTarget, string escapeMethod)
        where T : class
    {
        var module = await _module.Value;
        var handle = await module.InvokeAsync<IJSObjectReference>("trapFocus", container, escapeTarget, escapeMethod);
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
    /// Calls a component method when Escape is pressed until the returned
    /// handle is disposed. Intended for non-modal drawers and popovers that do
    /// not trap focus.
    /// </summary>
    public async Task<IAsyncDisposable> BindEscapeAsync<T>(DotNetObjectReference<T> reference, string method)
        where T : class
    {
        var module = await _module.Value;
        var handle = await module.InvokeAsync<IJSObjectReference>("bindEscape", reference, method);
        return new Handle(handle);
    }

    /// <summary>
    /// Focuses the element on Ctrl+key (⌘+key on a Mac), for as long as the
    /// returned handle lives. Handled in the browser, so no keystroke reaches
    /// the server.
    /// </summary>
    public async Task<IAsyncDisposable> BindFocusShortcutAsync(ElementReference element, string key)
    {
        var module = await _module.Value;
        var handle = await module.InvokeAsync<IJSObjectReference>("bindFocusShortcut", element, key);
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

    /// <summary>Downloads a media URL without navigating away. Returns false when the browser cannot fetch it.</summary>
    public async Task<bool> DownloadAsync(string url, string fileName)
    {
        var module = await _module.Value;
        return await module.InvokeAsync<bool>("downloadFile", url, fileName);
    }

    /// <summary>
    /// Keeps Enter from adding a newline to <paramref name="textarea"/> until
    /// disposed, so the component's keydown handler can send instead.
    /// </summary>
    public async Task<IAsyncDisposable> SubmitOnEnterAsync(ElementReference textarea)
    {
        var module = await _module.Value;
        var handle = await module.InvokeAsync<IJSObjectReference>("submitOnEnter", textarea);
        return new Handle(handle);
    }

    /// <summary>Grows a textarea with its text up to its CSS max-height, until disposed.</summary>
    public async Task<IAsyncDisposable> AutoGrowAsync(ElementReference textarea)
    {
        var module = await _module.Value;
        var handle = await module.InvokeAsync<IJSObjectReference>("autoGrow", textarea);
        return new Handle(handle);
    }

    /// <summary>Sets a field's text directly, for fields left uncontrolled so renders cannot overwrite typing.</summary>
    /// <summary>Inserts text at the field's caret (or over its selection) and returns the field's new text.</summary>
    public async Task<string?> InsertAtCaretAsync(ElementReference element, string text)
    {
        var module = await _module.Value;
        return await module.InvokeAsync<string?>("insertAtCaret", element, text);
    }

    public async Task SetValueAsync(ElementReference element, string value, bool focus = false)
    {
        var module = await _module.Value;
        await module.InvokeVoidAsync("setValue", element, value, focus);
    }

    /// <summary>
    /// Keeps room at the end of <paramref name="scroller"/> for a footer that
    /// floats over it, growing the CSS length <paramref name="property"/> as the
    /// footer grows, until the handle is disposed.
    /// </summary>
    public async Task<IAsyncDisposable> ReserveUnderAsync(ElementReference scroller, ElementReference footer, string property)
    {
        var module = await _module.Value;
        var handle = await module.InvokeAsync<IJSObjectReference>("reserveUnder", scroller, footer, property);
        return new Handle(handle);
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

    public async Task RemoveStorageAsync(string key)
    {
        var module = await _module.Value;
        await module.InvokeVoidAsync("removeStorage", key);
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
