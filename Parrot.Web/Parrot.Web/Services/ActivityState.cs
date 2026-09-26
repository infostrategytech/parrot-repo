namespace Parrot.Web.Services;

// How many API calls this circuit has in flight. The shell's hairline reads
// it, so every screen gets a loading cue without wiring one up.
public sealed class ActivityState
{
    private int _inFlight;

    public event Action? Changed;

    public bool IsBusy => Volatile.Read(ref _inFlight) > 0;

    public void Begin()
    {
        if (Interlocked.Increment(ref _inFlight) == 1)
        {
            Changed?.Invoke();
        }
    }

    public void End()
    {
        if (Interlocked.Decrement(ref _inFlight) == 0)
        {
            Changed?.Invoke();
        }
    }
}
