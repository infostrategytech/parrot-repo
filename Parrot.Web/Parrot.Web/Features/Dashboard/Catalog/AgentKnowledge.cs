namespace Parrot.Web.Features.Dashboard.Catalog;

/// <summary>
/// What the Products list says about where the agent is with each item (designer, 23 November 2026): nothing
/// when it knows the item, and a plain word only when the owner has something to fix. Built on the AI team's
/// ingestion report the catalog already returns (item.ai); nothing here needs the backend.
/// </summary>
public static class AgentKnowledge
{
    /// <summary>How long a failure on the agent's side may retry by itself before the owner is told.</summary>
    public static readonly TimeSpan PatienceForFailures = TimeSpan.FromDays(1);

    public static int RejectedPhotos(CatalogEntry item) => item.AiPhotos.Count(photo => photo.Rejected);

    /// <summary>Why the photos weren't used, in the agent's words, or a plain fallback.</summary>
    public static string PhotoWhy(CatalogEntry item) =>
        string.Join(" ", item.AiPhotos.Where(photo => photo.Rejected).Select(photo => photo.Reason)
            .OfType<string>().Where(reason => reason.Length > 0).Distinct()
            .DefaultIfEmpty("It doesn't seem to show the item."));

    /// <summary>A failure on the agent's side that has gone on long enough to mention.</summary>
    public static bool StuckFailing(CatalogEntry item) =>
        item.Ai == AiProgress.Failed && item.AiReportedAt is { } at && DateTimeOffset.UtcNow - at > PatienceForFailures;

    /// <summary>An item the owner should look at: a photo or detail the agent couldn't use.</summary>
    public static bool NeedsLook(CatalogEntry item) => item.Ai == AiProgress.NeedsAttention;

    /// <summary>The line above the list when items need a look.</summary>
    public static string NeedsLookLine(IReadOnlyCollection<CatalogEntry> items)
    {
        int count = items.Count;
        bool photos = items.All(item => RejectedPhotos(item) > 0);
        bool details = items.All(item => RejectedPhotos(item) == 0);
        return (count, photos, details) switch
        {
            (1, true, _) => RejectedPhotos(items.First()) == 1
                ? "1 item has a photo your agent didn't use."
                : "1 item has photos your agent didn't use.",
            (_, true, _) => $"{count} items have photos your agent didn't use.",
            (1, _, true) => "1 item has details your agent couldn't use.",
            (_, _, true) => $"{count} items have details your agent couldn't use.",
            _ => $"{count} items have photos or details your agent couldn't use.",
        };
    }

    /// <summary>Takes the agent's latest report from a fresh read, leaving everything else on the page as it is.</summary>
    public static void TakeReport(CatalogEntry into, CatalogEntry from)
    {
        into.Ai = from.Ai;
        into.AiPhotos = [.. from.AiPhotos];
        into.AiTextReason = from.AiTextReason;
        into.AiReportedAt = from.AiReportedAt;
    }
}
