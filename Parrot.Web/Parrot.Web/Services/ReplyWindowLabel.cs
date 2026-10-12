namespace Parrot.Web.Services;

/// <summary>The reply window's time left, as the inbox words it.</summary>
public static class ReplyWindowLabel
{
    public static string Describe(DateTime endsAt, DateTime now)
    {
        TimeSpan left = endsAt - now;
        if (left <= TimeSpan.Zero) return "Reply window closed";
        if (left < TimeSpan.FromMinutes(1)) return "Under 1m left to reply";
        if (left < TimeSpan.FromHours(1)) return $"{(int)left.TotalMinutes}m left to reply";
        return $"{(int)left.TotalHours}h {left.Minutes}m left to reply";
    }
}
