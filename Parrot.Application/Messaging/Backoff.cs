namespace Parrot.Application.Messaging;

/// <summary>
/// Spreads a retry wait by up to a quarter either way, so a burst of jobs that failed together (a platform outage,
/// storage restarting) does not come back together and fail again.
/// </summary>
public static class Backoff
{
    /// <param name="sample">A number from 0 up to 1. 0.5 leaves the wait unchanged.</param>
    public static TimeSpan Jitter(TimeSpan wait, double sample) => wait * (0.75 + (0.5 * Math.Clamp(sample, 0, 1)));
}
