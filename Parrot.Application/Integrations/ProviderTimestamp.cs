namespace Parrot.Application.Integrations;

// Meta sends the send-time in different units per platform: WhatsApp in whole seconds, Messenger in
// milliseconds. Both arrive as opaque strings, so the difference is invisible at the call site and a
// cross-platform comparison is silently wrong by 1000x. Normalise once, here, at the edge.
public static class ProviderTimestamp
{
    // Anything outside this window is a unit error or corruption, not a real send-time. The range is
    // checked before converting because DateTimeOffset.FromUnixTime* throws on out-of-range input
    // rather than returning a nonsense date — a malformed value would otherwise fault the webhook.
    private static readonly DateTime Floor = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static DateTime? FromUnixSeconds(string? value) =>
        long.TryParse(value, out long seconds) ? FromUnixSeconds(seconds) : null;

    public static DateTime? FromUnixSeconds(long seconds) =>
        seconds >= new DateTimeOffset(Floor).ToUnixTimeSeconds()
        && seconds <= DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds()
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
            : null;

    public static DateTime? FromUnixMilliseconds(long milliseconds) =>
        milliseconds >= new DateTimeOffset(Floor).ToUnixTimeMilliseconds()
        && milliseconds <= DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds()
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime
            : null;
}
