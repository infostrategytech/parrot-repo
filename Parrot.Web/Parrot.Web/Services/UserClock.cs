using Parrot.SharedComponents.Services;

namespace Parrot.Web.Services;

/// <summary>
/// What "today" is for the person using the app (designer, 20 October 2026): due dates say Today and
/// Tomorrow by the business's clock, not the server's (a cloud server runs on UTC). The workspace's own
/// time zone wins once the backend keeps it (workspace-backend-requests); until then, the browser's.
/// </summary>
public sealed class UserClock(AuroraInterop interop)
{
    private bool _read;

    /// <summary>The zone in use: the server's until the browser has said, then the browser's.</summary>
    public TimeZoneInfo Zone { get; private set; } = TimeZoneInfo.Local;

    public event Action? Changed;

    /// <summary>Asks the browser once (after the first render: there is no browser while prerendering).</summary>
    public async Task ReadAsync()
    {
        if (_read) return;
        _read = true;
        try
        {
            if (await interop.TimeZoneAsync() is { Length: > 0 } id && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone))
            {
                Zone = zone;
                Changed?.Invoke();
            }
        }
        catch (Exception exception) when (exception is Microsoft.JSInterop.JSException or InvalidOperationException or TaskCanceledException) { }
    }

    public DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

    public DateTime ToUtc(DateTime local) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Zone);

    public DateOnly Today => DateOnly.FromDateTime(ToLocal(DateTime.UtcNow));
}
