using System.Text.RegularExpressions;

namespace Parrot.Web.Features.Dashboard.Catalog.ConceptE;

/// <summary>
/// Bringing a post in (designer, 26 October 2026: keep it simple). A post arrives as a photo and a caption;
/// it's read, and what was read is put where it belongs on an ordinary item: the name, the price, and, when
/// the price changes with a choice (UK 6–12 and 14–20, 8 or 14 inches), a price for each choice. The owner
/// checks it on the item's own form. Posts already on the list and old posts are left out of the choice, so
/// nothing is added twice and no old price slips in.
/// </summary>
public sealed class PostDraft
{
    public required SocialPost Post { get; init; }
    public required CatalogEntry Item { get; init; }

    /// <summary>What to check, in plain words: an old post, no price, possibly more than one item.</summary>
    public List<string> Checks { get; init; } = [];

    /// <summary>The item already on the list that this post is about, if any: saving updates it.</summary>
    public CatalogEntry? Match { get; init; }

    /// <summary>The post may show more than one item ("Option 1, Option 2").</summary>
    public bool MaybeMany { get; init; }

    public static PostDraft From(SocialPost post, CaptionReading reading, IEnumerable<CatalogEntry> items)
    {
        var item = new CatalogEntry
        {
            Kind = reading.Kind ?? CatalogKind.Product,
            Name = reading.Name,
            // Keep the owner's caption intact and editable; extraction fills structured fields separately.
            Description = post.Caption,
            Photos = post.ImageUrl.Length > 0 ? [post.ImageUrl] : [],
            Status = CatalogStatus.Draft,
            IsAvailable = true,
            // Selecting a post to import defaults a new item to available; the owner can change it in review.
            AvailabilityIsUnconfirmed = false,
        };
        reading.ApplyOffer(item);
        if (item.Kind == CatalogKind.Service) item.Duration = reading.LeadTime;
        else { item.LeadTime = reading.LeadTime; item.MadeToOrder = reading.MadeToOrder; }

        var checks = new List<string>();
        if (item.Name.Trim().Length == 0) checks.Add("No name in the post. Add one.");
        if (PriceWords.For(item).Missing) checks.Add("No price in the post. Add one.");
        else if (post.DaysAgo >= 14) checks.Add($"Posted {Age(post.DaysAgo)} ago. Is the price still right?");
        var many = reading.PriceOption is { Name: "Option" };
        if (many) checks.Add("This post may show more than one item. Add the others on their own.");
        checks.AddRange(reading.Checks);

        return new PostDraft { Post = post, Item = item, Checks = checks, Match = Existing(post, items), MaybeMany = many };
    }

    /// <summary>
    /// The item already on the list, with what the post changes: its price and choices (when the post has a
    /// price) and its photo. Its name, category and words stay. What "Update it" saves.
    /// </summary>
    public static CatalogEntry Onto(CatalogEntry match, CatalogEntry from)
    {
        var entry = match.Copy();
        if (!PriceWords.For(from).Missing)
        {
            entry.PriceType = from.PriceType;
            entry.Price = from.Price;
            // "per person" stays when the new words don't say what the price is per.
            if (from.PriceUnit.Trim().Length > 0) entry.PriceUnit = from.PriceUnit;
            if (from.Options.Count > 0)
            {
                // The versions in the post, as written: never every combination of their values. A version
                // already on the item keeps its stock and whether it's sold out.
                entry.Options = [.. from.Options.Select(option => option.Copy())];
                var before = entry.Variants;
                entry.Variants = [.. from.Variants.Select(variant =>
                {
                    var kept = before.FirstOrDefault(other => string.Equals(other.Key, variant.Key, StringComparison.OrdinalIgnoreCase));
                    return new ItemVariant { Key = variant.Key, Price = variant.Price, IsAvailable = kept?.IsAvailable ?? variant.IsAvailable, Stock = kept?.Stock };
                })];
            }
            else
            {
                foreach (var variant in entry.Variants) variant.Price = null;
            }
            entry.Status = CatalogStatus.Live;
        }
        entry.Photos = [.. entry.Photos.Concat(from.Photos.Where(photo => !entry.Photos.Contains(photo))).Take(4)];
        if (entry.Description.Trim().Length == 0) entry.Description = from.Description;
        return entry;
    }

    /// <summary>
    /// The item on the list this post is about, from the words they share ("Our cotton adire scarves" is the
    /// Adire scarf). A stand-in: the backend should link posts to items and match them (catalog-backend-requests).
    /// </summary>
    public static CatalogEntry? Existing(SocialPost post, IEnumerable<CatalogEntry> items)
    {
        var caption = Words(post.Caption);
        return items
            .Select(item => (item, words: Words(item.Name)))
            .Where(pair => pair.words.Count > 0)
            .Select(pair => (pair.item, shared: pair.words.Count(word => caption.Any(other => Same(word, other))), pair.words.Count))
            .Where(match => match.shared >= Math.Min(2, match.Count) && match.shared * 3 >= match.Count * 2)
            // The closest: all of a short name ("Adire scarf") beats most of a longer one ("Indigo adire kaftan").
            .OrderByDescending(match => match.shared / (double)match.Count)
            .ThenByDescending(match => match.shared)
            .Select(match => match.item)
            .FirstOrDefault();
    }

    /// <summary>Older than this, a post is left out of the choice unless asked for: its price is likely out of date.</summary>
    public const int OldAfterDays = 90;

    /// <summary>
    /// A new post to review: one the owner hasn't seen yet (not shown to them on Select posts, brought in or
    /// dismissed), not already added, and not over 3 months old: the same posts Select posts shows, less seen ones.
    /// </summary>
    public static bool IsNew(SocialPost post, IEnumerable<CatalogEntry> items, IReadOnlyCollection<string> seen) =>
        post.DaysAgo <= OldAfterDays && !seen.Contains(post.Id) && Existing(post, items) is null;

    public static string Age(int days) => days switch
    {
        < 1 => "today",
        < 2 => "1 day",
        < 7 => $"{days} days",
        < 14 => "1 week",
        < 31 => $"{days / 7} weeks",
        < 60 => "1 month",
        _ => $"{days / 30} months",
    };

    public static string Ago(int days) => days < 1 ? "Today" : $"{char.ToUpperInvariant(Age(days)[0])}{Age(days)[1..]} ago";

    private static List<string> Words(string text) =>
        [.. Regex.Split(text.ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(word => word.Length >= 4 && !Common.Contains(word))];

    // "scarf" and "scarves", "class" and "classes": the same word.
    private static bool Same(string a, string b) => a == b || a.Length >= 4 && b.Length >= 4 && a[..4] == b[..4] && Math.Abs(a.Length - b.Length) <= 3;

    private static readonly HashSet<string> Common = ["with", "from", "your", "this", "that", "have", "available", "price", "each", "every", "free", "size", "sizes", "order", "orders", "today", "back", "send"];
}

/// <summary>How a price list comes in: a photo of it, an Excel or CSV file, or words pasted in.</summary>
public enum ImportSource { Photo, Sheet, Paste }

/// <summary>
/// More posts for the demo, from the research (products-pricing-research.md), so bringing posts in shows what
/// vendors really write: a price for each size, a price list by length, a TikTok list on one line, and an old
/// post. They have no photo (the demo has none that fits), as a video post may show only its words here. Real
/// posts come only from the connected channels, through IOfferingDiscovery.
/// </summary>
public static class DemoPosts
{
    public static readonly IReadOnlyList<SocialPost> More =
    [
        new("ig-chelsea", "instagram", "",
            "Introducing CHELSEA In this Gorgeous Grey\nWe are strictly a UK sizing brand. Please refer to our size chart.\nLength can be adjusted when ordering.\nPrice;\nUK size 6 - 12: 58,500\nUK size14- 20: 62,000\nKindly send a dm or click the link in our bio to order via WhatsApp",
            "Instagram · Today", "https://www.instagram.com/p/chelsea", 0),
        new("tt-perfume", "tiktok", "",
            "Oud perfume oil 10ml - 7k  30ml - 20k 50ml - 25k 100ml - 45k #perfumeoil #lagosvendor",
            "TikTok · 1 day ago", "https://www.tiktok.com/@demo/video/1", 1),
        new("tt-wigs", "tiktok", "",
            "Bob wigs pricelist   8 inches- 110k only 10 inches 120k only 12 inches 135k only 14 inches 155k only",
            "TikTok · 4 days ago", "https://www.tiktok.com/@demo/video/2", 4),
        // From a Lagos fashion brand's reel (designer, 27 October 2026), without its name or website: two size
        // ranges at the same price, after a hook line.
        new("ig-queency", "instagram", "",
            "BLACK but make it OUTSTANDING 🖤🖤🖤\n\nIntroducting QUEENCY💃💃💃💃💃\n\nEnjoy 5% off when you shop from our website\n_______________________\nWe are strictly a UK sizing brand…\nPlease refer to our size chart to reconfirm your size and measurements for an accurate fit📌📌\n\nWe have a standard length for Pants and Dresses (if you want a shorter or longer length confirm while ordering)\n\nPrice; \nUK size 6 - 12: 50,500\nUK size14- 20: 50,500\nKindly send a dm or click on the link in our bio to order directly via WhatsApp",
            "Instagram · 1 hour ago", "https://www.instagram.com/p/queency", 0),
        // From a Lagos foodstuff store's TikTok (designer, 27 October 2026), with a made-up number and address: a price
        // for each bag size, and a gift post with no price at all.
        new("tt-rice", "tiktok", "",
            "MAs choice rice\n50kg 70,000\n25kg 35,000\n12.5kg 18,500\n10kg 14,500\n5kg 7,500\n\nSend a DM or WhatsApp 08000000000 to place your order\nShop address: Ikeja, Lagos",
            "TikTok · 2 days ago", "https://www.tiktok.com/@demo/video/3", 2),
        new("tt-hamper", "tiktok", "",
            "THIS CHRISTMAS, GIVE THE GIFT OF FOOD! 💗\n\nPerfume is lovely, but a gift that helps stock someone's kitchen? That's thoughtful too!\n\nSurprise your parents, appreciate a friend, gift your staff or support a family in need with rice from us.\n\n📲 WhatsApp: 08000000000\n📍 Ikeja, Lagos\n🚚 delivery nationwide\n\n#RiceGift #ChristmasGifting #ChristmasFoodstuff",
            "TikTok · 1 day ago", "https://www.tiktok.com/@demo/video/4", 1),
        // Versions named by more than one thing, each at its own price: only these exist (no UK 6 in red).
        new("ig-ankara-set", "instagram", "",
            "Ankara two-piece set\nUk 6 blue 50k\nUk 12 red 55k\nUk 14 yellow 64k\nDm to order. Delivery nationwide.",
            "Instagram · 2 days ago", "https://www.instagram.com/p/ankara-set", 2),
        new("ig-iphone", "instagram", "",
            "iPhone 14 Pro\n128gb blue brand new - 1.35m\n128gb blue UK used - 1.1m\n256gb black UK used - 1.25m\n256gb gold brand new - 1.55m\nSwap available. Dm to order.",
            "Instagram · 3 days ago", "https://www.instagram.com/p/iphone-14-pro", 3),
        new("ig-old-agbada", "instagram", "/demo/products/kaftan.jpg",
            "Festive agbada, hand-dyed, ₦85,000. Made to order, ready in 10 days.",
            "Instagram · 4 months ago", "https://www.instagram.com/p/agbada", 124),
    ];
}
