namespace Parrot.Web.Features.Dashboard.Catalog;

/// <summary>
/// Reads content the business has already shared and prepares catalog drafts.
/// Screens depend on this contract rather than a social network SDK, so demo
/// discovery can later be replaced by API-backed channel readers.
/// </summary>
public interface IOfferingDiscovery
{
    /// <summary>
    /// Whether posts can be read at all. The page offers "From your posts"
    /// only when they can, as Channels offers only what can connect.
    /// </summary>
    bool CanReadPosts { get; }

    Task<IReadOnlyList<SocialPost>> ListRecentPostsAsync(CancellationToken cancellationToken = default);

    Task<OfferingDiscoveryResult> AnalyzePostsAsync(
        IReadOnlyCollection<string> postIds,
        CancellationToken cancellationToken = default);

    Task<OfferingDiscoveryResult> AnalyzeLinkAsync(
        string postUrl,
        CancellationToken cancellationToken = default);
}

/// <param name="Channel">One of the business's connected channels: "instagram" or "facebook".</param>
/// <param name="DaysAgo">How old the post is, so an old price can be checked before it is used.</param>
public sealed record SocialPost(
    string Id,
    string Channel,
    string ImageUrl,
    string Caption,
    string PublishedLabel,
    string Permalink,
    int DaysAgo = 0);

public enum OfferingSuggestionState
{
    Ready,
    NeedsCheck,
    PossibleDuplicate,
}

/// <summary>A draft fact set plus the exact social source it came from.</summary>
public sealed class OfferingSuggestion
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public CatalogEntry Draft { get; init; } = new();
    public OfferingSuggestionState State { get; set; }
    public string SourceChannel { get; init; } = "";
    public string SourcePostId { get; init; } = "";
    public string SourceUrl { get; init; } = "";
    public DateTimeOffset? SourcePublishedAt { get; init; }
    public string SourceLabel { get; init; } = "";
    public string SourceImageUrl { get; init; } = "";
    public string SourceExcerpt { get; init; } = "";
    public int SourceDaysAgo { get; init; }
    public string? CheckMessage { get; set; }

    /// <summary>The item already on the list with the same name, when there is one.</summary>
    public string? DuplicateName { get; set; }
    public string? DuplicateId { get; set; }

    /// <summary>The owner chose to update that item's price and photo instead of adding a new one.</summary>
    public bool UpdateExisting { get; set; }

    /// <summary>The check is "is this old price still right?" rather than "there is no price".</summary>
    public bool OldPrice { get; set; }
}

public sealed record OfferingDiscoveryResult(
    IReadOnlyList<OfferingSuggestion> Suggestions,
    int SourcesRead);

/// <summary>
/// Real mode until the channel-content and extraction endpoints are provided.
/// Keeping the unavailable implementation explicit prevents a page from
/// silently pretending social content was imported.
/// </summary>
public sealed class UnavailableOfferingDiscovery : IOfferingDiscovery
{
    public bool CanReadPosts => false;

    public Task<IReadOnlyList<SocialPost>> ListRecentPostsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SocialPost>>([]);

    public Task<OfferingDiscoveryResult> AnalyzePostsAsync(
        IReadOnlyCollection<string> postIds,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Social offering discovery is not connected yet.");

    public Task<OfferingDiscoveryResult> AnalyzeLinkAsync(
        string postUrl,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Social offering discovery is not connected yet.");
}
