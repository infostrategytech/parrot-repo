using Parrot.Web.Features.Dashboard.Channels.Models;

namespace Parrot.Web.Features.Dashboard.Catalog.ConceptE;

/// <summary>
/// One item to review before it's added (designer, 26 October 2026): from a post or a price list, the review
/// is the same table. It holds the item as it will be saved (or the item on the list it updates, with what
/// changes), whether it's ticked to add, and where it came from.
/// </summary>
public sealed class ReviewDraft
{
    public required string Key { get; init; }

    /// <summary>The item as it will be saved: new, or the match with what came in.</summary>
    public required CatalogEntry Item { get; set; }

    /// <summary>The item already on the list this is about, if any.</summary>
    public CatalogEntry? Match { get; init; }

    /// <summary>Saving updates <see cref="Match"/> rather than adding a new item.</summary>
    public bool Update { get; set; }

    public bool Include { get; set; } = true;

    /// <summary>Explicitly confirmed on the item's form; extraction alone never makes an item ready.</summary>
    public bool Reviewed { get; set; }

    public bool Ready => Reviewed && CatalogReviewValidation.Issue(Item) is null;

    /// <summary>The post it came from, when it came from one.</summary>
    public SocialPost? Post { get; init; }

    /// <summary>What to check, in plain words, shown on the item's form.</summary>
    public IReadOnlyList<string> Checks { get; init; } = [];

    public bool MaybeMany { get; init; }

    /// <summary>A price-list line that was hard to read.</summary>
    public bool HardToRead { get; init; }

    /// <summary>What came in had no price, so an item on the list keeps its own.</summary>
    public bool NoNewPrice { get; init; }

    public static ReviewDraft FromPost(PostDraft draft) => new()
    {
        Key = draft.Post.Id,
        Post = draft.Post,
        Checks = draft.Checks,
        Match = draft.Match,
        MaybeMany = draft.MaybeMany,
        NoNewPrice = PriceWords.For(draft.Item).Missing,
        // Already on the list: it updates that item, with the post's price and photo.
        Item = draft.Match is { } match ? PostDraft.Onto(match, draft.Item) : draft.Item,
        Update = draft.Match is not null,
        // One that would change nothing starts unticked, as on a price list.
        Include = draft.Match is not { } same || Changes(same, PostDraft.Onto(same, draft.Item)),
    };

    /// <summary>
    /// A price-list line: a new item, or the item on the list it names with its new price. One whose price is
    /// the same, or that has none to give, changes nothing, so it starts unticked.
    /// </summary>
    public static ReviewDraft FromLine(PriceListLine line, int index, IReadOnlyList<CatalogEntry> items)
    {
        var item = new CatalogEntry
        {
            Kind = line.Kind,
            Name = line.Name.Trim(),
            Price = line.Price,
            PriceType = line.From ? PriceType.From : PriceType.Fixed,
            IsAvailable = true,
            Status = line.Price is null ? CatalogStatus.Draft : CatalogStatus.Live,
        };
        var match = MatchOf(line.Name, items);
        var checks = line.NeedsCheck ? new List<string> { "This line was hard to read. Check the name and price." } : [];
        if (match is null) return new ReviewDraft { Key = $"line-{index}", Item = item, Checks = checks, HardToRead = line.NeedsCheck, NoNewPrice = line.Price is null };

        var updated = PostDraft.Onto(match, item);
        return new ReviewDraft
        {
            Key = $"line-{index}",
            Item = updated,
            Match = match,
            Update = true,
            Include = Changes(match, updated),
            Checks = checks,
            HardToRead = line.NeedsCheck,
            NoNewPrice = line.Price is null,
        };
    }

    /// <summary>Whether saving would change the item on the list: its price, said the way the table says it.</summary>
    public static bool Changes(CatalogEntry match, CatalogEntry item) =>
        !PriceWords.For(item).Missing && (PriceWords.For(match) != PriceWords.For(item) || !PriceWords.PriceList(match).SequenceEqual(PriceWords.PriceList(item)));

    private static CatalogEntry? MatchOf(string name, IReadOnlyList<CatalogEntry> items) =>
        name.Trim().Length == 0 ? null
        : items.FirstOrDefault(item => string.Equals(item.Name.Trim(), name.Trim(), StringComparison.CurrentCultureIgnoreCase))
          ?? PostDraft.Existing(new SocialPost("", "", "", name, "", ""), items);
}

/// <summary>The same readiness check guards individual confirmation and the final batch save.</summary>
public static class CatalogReviewValidation
{
    public static string? Issue(CatalogEntry item) => CatalogReadiness.Issue(item);
}

/// <summary>Where the items being reviewed came from, said once above the table.</summary>
public sealed record ReviewSource(ImportSource? List, IReadOnlyList<string> Channels, string? Photo = null, string? FileName = null)
{
    public static ReviewSource Posts(IEnumerable<SocialPost> posts) =>
        new(null, [.. PostChannels.Social.Where(channel => posts.Any(post => post.Channel == channel))]);
}

/// <summary>
/// The social channels posts can come from, as connected (designer, 26 October 2026: the posts, their tabs and
/// marks follow what's connected). Real posts come only from the connected channels (catalog-backend-requests).
/// </summary>
public static class PostChannels
{
    public static readonly IReadOnlyList<string> Social = ["instagram", "facebook", "tiktok"];

    /// <param name="preview">The demo's ?channels= ("none", or a list such as "instagram,tiktok").</param>
    public static IReadOnlyList<string> Connected(ChannelWorkspace workspace, bool demo, string? preview)
    {
        if (demo && preview is { Length: > 0 })
            return preview == "none" ? [] : [.. Social.Where(channel => preview.Split(',', StringSplitOptions.TrimEntries).Contains(channel, StringComparer.OrdinalIgnoreCase))];
        var connected = Social
            .Where(channel => workspace.Channels.Any(each => each.Status == ChannelStatus.Connected && each.Id.ToString().Equals(channel, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        // The demo, with nothing connected in this session: as if all three were, so posts can be tried.
        return connected.Count == 0 && demo ? Social : connected;
    }

    public static string Name(string channel) => channel.ToLowerInvariant() switch
    {
        "instagram" => "Instagram",
        "facebook" => "Facebook",
        "tiktok" => "TikTok",
        "whatsapp" => "WhatsApp",
        _ => "Your post",
    };

    /// <summary>"Instagram", "Instagram and TikTok", "Instagram, Facebook and TikTok" (or "or").</summary>
    public static string Names(IEnumerable<string> channels, string joiner = "and")
    {
        var names = channels.Select(Name).ToList();
        return names.Count switch
        {
            0 => string.Empty,
            1 => names[0],
            _ => $"{string.Join(", ", names.Take(names.Count - 1))} {joiner} {names[^1]}",
        };
    }
}
