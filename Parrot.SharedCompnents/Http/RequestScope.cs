namespace Parrot.SharedComponents.Http;

/// <summary>
/// One in-flight request at a time.
///
/// This replaces the AbortController that appeared nine times in React, always
/// written out inline with the same three lines of cleanup. It does two jobs
/// that look like one: it cancels a request that has been superseded, and it
/// stops a late reply from overwriting fresher state — which is the bug you
/// get when someone types quickly and an early, slower response lands last.
///
/// Hold one per component as a field, call <see cref="Next"/> before each
/// request, and dispose it in the component's Dispose.
/// </summary>
public sealed class RequestScope : IDisposable
{
    private CancellationTokenSource? _current;

    /// <summary>Cancels whatever was in flight and returns a token for the new request.</summary>
    public CancellationToken Next()
    {
        _current?.Cancel();
        _current?.Dispose();
        _current = new CancellationTokenSource();
        return _current.Token;
    }

    /// <summary>
    /// True when an exception is just this scope doing its job. Catch it and
    /// return, rather than showing an error for a request nobody wanted.
    /// </summary>
    public static bool WasCancelled(Exception exception) =>
        exception is OperationCanceledException;

    public void Dispose()
    {
        _current?.Cancel();
        _current?.Dispose();
        _current = null;
    }
}
