using Parrot.SharedComponents.DesignSystem;

namespace Parrot.SharedComponents.Services;

/// <param name="Title">The line that is always read out.</param>
/// <param name="Description">Optional second line.</param>
/// <param name="ActionLabel">Optional single action; null means no button.</param>
public sealed record ToastMessage(
    string Title,
    string? Description = null,
    ToastStatus Status = ToastStatus.Success,
    string? ActionLabel = null,
    Action? OnAction = null);

/// <summary>
/// The single toast slot.
///
/// One slot, not a queue — that is what React did, and stacking notifications
/// was never designed. Showing a toast while one is visible replaces it and
/// restarts the timer.
///
/// The dismiss delay lives here as one constant. In React each of five pages
/// ran its own timer, and one of them used 3000ms against the other four at
/// 3200ms; nobody chose that, it was a typo that became an inconsistency.
/// </summary>
public sealed class ToastService : IDisposable
{
    private static readonly TimeSpan DismissAfter = TimeSpan.FromMilliseconds(3200);

    private CancellationTokenSource? _dismiss;

    public ToastMessage? Current { get; private set; }

    /// <summary>Raised on the renderer's thread is NOT guaranteed — subscribers must marshal.</summary>
    public event Action? Changed;

    public void Show(ToastMessage message)
    {
        Current = message;
        Changed?.Invoke();
        RestartTimer();
    }

    public void Show(string title, ToastStatus status = ToastStatus.Success, string? description = null) =>
        Show(new ToastMessage(title, description, status));

    /// <summary>
    /// An error outranks a success: the React pages rendered
    /// <c>error || toast</c>, so a failed save was never hidden by the
    /// optimistic message that preceded it.
    /// </summary>
    public void ShowError(string message) =>
        Show(new ToastMessage(message, Status: ToastStatus.Error));

    public void Dismiss()
    {
        _dismiss?.Cancel();
        if (Current is null) return;
        Current = null;
        Changed?.Invoke();
    }

    private void RestartTimer()
    {
        _dismiss?.Cancel();
        _dismiss?.Dispose();
        var cts = new CancellationTokenSource();
        _dismiss = cts;

        _ = DismissLaterAsync(cts.Token);
    }

    private async Task DismissLaterAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(DismissAfter, token);
        }
        catch (TaskCanceledException)
        {
            // Replaced or dismissed by hand before the delay elapsed.
            return;
        }

        Current = null;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _dismiss?.Cancel();
        _dismiss?.Dispose();
        _dismiss = null;
    }
}
