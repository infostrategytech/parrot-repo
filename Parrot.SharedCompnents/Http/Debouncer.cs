namespace Parrot.SharedComponents.Http;

/// <summary>
/// Waits for a pause before doing the work.
///
/// The inbox filters are all server-side, so search-as-you-type would be one
/// request per keystroke without this. React nested a 140ms setTimeout inside
/// its AbortController and cleared both on cleanup; this is the same 140ms,
/// written once.
///
/// It composes with <see cref="RequestScope"/> rather than replacing it: the
/// debounce stops requests being made, the scope cancels the one that was.
/// </summary>
public sealed class Debouncer(int milliseconds = 140) : IDisposable
{
    private CancellationTokenSource? _pending;

    /// <summary>
    /// Runs <paramref name="action"/> once the caller stops asking for
    /// <paramref name="milliseconds"/>. Each call cancels the one before.
    /// </summary>
    public async Task RunAsync(Func<Task> action)
    {
        _pending?.Cancel();
        _pending?.Dispose();
        var cts = new CancellationTokenSource();
        _pending = cts;

        try
        {
            await Task.Delay(milliseconds, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a later keystroke. Nothing to do.
            return;
        }

        await action();
    }

    public void Dispose()
    {
        _pending?.Cancel();
        _pending?.Dispose();
        _pending = null;
    }
}
