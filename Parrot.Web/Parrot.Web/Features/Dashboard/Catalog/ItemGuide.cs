using System.Globalization;
using Parrot.SharedComponents.DesignSystem;

namespace Parrot.Web.Features.Dashboard.Catalog;

/// <summary>Whether a customer can have an item: now, not for now (sold out, or not taking bookings), or not offered at all.</summary>
public enum ItemAvailability { Available, SoldOut, Hidden }

/// <summary>One thing an agent can do with an item, and what it needs to.</summary>
/// <param name="Section">Where in the item to add what is missing.</param>
public sealed record ItemAbility(string Name, bool Ready, string Hint, string Section, bool Core);

/// <summary>
/// The words and suggestions each kind of item uses, and what an agent can do
/// with an item as it stands. Data, not markup, so the list, the item page and
/// its preview all say the same thing.
/// </summary>
public static class ItemGuide
{
    public static string KindName(CatalogKind kind) => kind switch
    {
        CatalogKind.Food => "Food & drinks",
        CatalogKind.Service => "Service",
        CatalogKind.Digital => "Digital",
        _ => "Product",
    };

    // The icon set has no food mark yet (a design-system gap): food takes the shop front.
    public static IconName KindIcon(CatalogKind kind) => kind switch
    {
        CatalogKind.Food => IconName.RiStore2Fill,
        CatalogKind.Service => IconName.RiCalendarCheckFill,
        CatalogKind.Digital => IconName.RiDownload2Fill,
        _ => IconName.RiShoppingBag3Fill,
    };

    public static string KindExamples(CatalogKind kind) => kind switch
    {
        CatalogKind.Food => "Meals, cakes, small chops, drinks",
        CatalogKind.Service => "Tailoring, hair, repairs, classes",
        CatalogKind.Digital => "Ebooks, courses, templates, tickets",
        _ => "Clothes, bags, phones, beauty",
    };

    /// <summary>Labels customers ask about, offered as one-tap rows for the kind.</summary>
    public static IReadOnlyList<string> SuggestedFacts(CatalogKind kind) => kind switch
    {
        CatalogKind.Food => ["Ingredients", "Portion", "Spice level", "Allergens", "Serves"],
        CatalogKind.Service => ["What's included", "Who does it", "What to bring", "Results"],
        CatalogKind.Digital => ["Format", "Length", "What you get", "Access"],
        _ => ["Material", "Size", "Colour", "Care", "Made in"],
    };

    /// <summary>Questions customers ask about this kind of thing, offered as one-tap rows.</summary>
    public static IReadOnlyList<string> SuggestedQuestions(CatalogKind kind) => kind switch
    {
        CatalogKind.Food => ["Is it spicy?", "How many people does it feed?", "How long does it take?", "Do you deliver hot?"],
        CatalogKind.Service => ["How long does it take?", "Do I need to book ahead?", "Can you come to me?", "What do I need to bring?"],
        CatalogKind.Digital => ["How do I get it after paying?", "Can I use it on my phone?", "Is there a refund?"],
        _ => ["Is it original?", "What sizes do you have?", "How long is delivery?", "Can I return it?"],
    };

    public static string OptionHint(CatalogKind kind) => kind switch
    {
        CatalogKind.Food => "Like size (small, large) or flavour.",
        CatalogKind.Service => "Like style or length, if each has its own price.",
        CatalogKind.Digital => "Like format (PDF, print) or plan.",
        _ => "Like size or colour.",
    };

    public static string ExtraHint(CatalogKind kind) => kind switch
    {
        CatalogKind.Food => "Like a takeaway pack, extra protein or a drink.",
        CatalogKind.Service => "Like a home visit or a rush job.",
        CatalogKind.Digital => "Like a printed copy.",
        _ => "Like gift wrap or express delivery.",
    };

    public static string Naira(decimal amount) =>
        "₦" + Math.Round(amount, MidpointRounding.AwayFromZero).ToString("#,##0", CultureInfo.InvariantCulture);

    /// <summary>The price as the list shows it: "₦45,000", "From ₦25,000", "₦6,500 per yard", "Ask for a quote".</summary>
    public static string PriceText(CatalogEntry item)
    {
        if (item.PriceType == PriceType.OnRequest) return "Price on request";
        var prices = item.HasOptions
            ? item.Variants.Select(variant => variant.Price ?? item.Price).Where(price => price is not null).Select(price => price!.Value).ToList()
            : item.Price is { } single ? [single] : [];
        if (prices.Count == 0) return "No price yet";
        var low = prices.Min();
        var text = item.PriceType == PriceType.From || prices.Distinct().Count() > 1 ? $"From {Naira(low)}" : Naira(low);
        return item.PriceUnit.Length > 0 ? $"{text} {item.PriceUnit}" : text;
    }

    /// <summary>
    /// What an agent can do with the item as it stands (research: show
    /// readiness as abilities, not a score). The first three are the core:
    /// without them the item needs details before an agent can sell it.
    /// </summary>
    public static IReadOnlyList<ItemAbility> Abilities(CatalogEntry item)
    {
        var priced = item.PriceType == PriceType.OnRequest || item.Price is > 0 ||
                     (item.HasOptions && item.Variants.All(variant => variant.Price is > 0));
        var orderable = item.PriceType != PriceType.OnRequest &&
                        (item.HasOptions ? item.Variants.All(variant => (variant.Price ?? item.Price) is > 0) : item.Price is > 0);
        var known = item.Facts.Count(fact => fact.Label.Length > 0 && fact.Value.Length > 0)
                    + item.Answers.Count(answer => answer.Question.Length > 0 && answer.Answer.Length > 0)
                    + (item.Description.Trim().Length >= 40 ? 1 : 0);
        return
        [
            new("Quote the price", priced, "Add a price", "basics", Core: true),
            new("Take an order", orderable,
                item.PriceType == PriceType.OnRequest ? "It asks your team for the price first" : item.HasOptions ? "Price each option" : "Add a price",
                item.HasOptions ? "options" : "basics", Core: true),
            new("Answer questions", known >= 2, "Add 2 details or answers", "details", Core: true),
            new("Know what customers call it", item.OtherNames.Count > 0, "Add other names", "selling", Core: false),
            new("Offer something else", item.Related.Count > 0, "Choose related items", "selling", Core: false),
        ];
    }

    /// <summary>Ready for an agent to sell: the core abilities are all there.</summary>
    public static bool AgentReady(CatalogEntry item) => Abilities(item).Where(ability => ability.Core).All(ability => ability.Ready);

    /// <summary>On the list but not ready for an agent to sell: the list marks it "Needs details".</summary>
    public static bool NeedsDetails(CatalogEntry item) => item.Status == CatalogStatus.Live && !AgentReady(item);

    /// <summary>Switched on, but counted to zero everywhere: sold out, not paused.</summary>
    public static bool SoldOut(CatalogEntry item) =>
        item.IsAvailable && item.TracksStock && !item.MadeToOrder && (item.HasOptions
            ? item.Variants.All(variant => (variant.Stock ?? 0) <= 0)
            : (item.Stock ?? 0) <= 0);

    /// <summary>
    /// An item's second line on a list: its group (unless a column shows it),
    /// what kind it is, and the one fact that matters next (options, how
    /// long, made to order, stock). A list that shows stock in its own column leaves it out.
    /// </summary>
    public static string Meta(CatalogEntry item, bool withGroup = true, bool withStock = true)
    {
        var parts = new List<string>();
        if (withGroup && item.Group.Length > 0) parts.Add(item.Group);
        if (item.Status == CatalogStatus.Draft)
        {
            parts.Add(item.Price is null && item.PriceType != PriceType.OnRequest ? "Add a price to show it" : "Not shown to customers yet");
            return string.Join(" · ", parts);
        }
        parts.Add(KindName(item.Kind));
        if (item.HasOptions)
        {
            var option = item.Options[0];
            parts.Add(item.Options.Count == 1 && option.Values.Count <= 4
                ? string.Join(", ", option.Values)
                : $"{item.Variants.Count} options");
        }
        if (item.Kind == CatalogKind.Service && item.Duration.Length > 0) parts.Add(item.Duration);
        if (item.MadeToOrder) parts.Add(item.LeadTime.Length > 0 ? $"Made to order, {item.LeadTime.ToLowerInvariant()}" : "Made to order");
        else if (item.TracksStock && withStock)
        {
            var stock = item.HasOptions ? item.Variants.Sum(variant => Math.Max(0, variant.Stock ?? 0)) : Math.Max(0, item.Stock ?? 0);
            parts.Add(stock > 0 ? $"{stock} in stock" : "None left. Add stock to sell it");
        }
        return string.Join(" · ", parts);
    }

    /// <summary>Whether a customer can have it: now, not for now, or not offered at all (inactive).</summary>
    public static ItemAvailability AvailabilityOf(CatalogEntry item) =>
        item.IsHidden ? ItemAvailability.Hidden
        : InStock(item) ? ItemAvailability.Available
        : ItemAvailability.SoldOut;

    /// <summary>In stock as far as stock goes (switched on, and some left where it is counted), active or not.</summary>
    public static bool InStock(CatalogEntry item) =>
        item.IsAvailable && (item.HasOptions
            ? item.Variants.Any(variant => variant.IsAvailable && (!item.TracksStock || variant.Stock > 0))
            : item.MadeToOrder || item.Kind is CatalogKind.Service or CatalogKind.Digital || !item.TracksStock || item.Stock > 0);

    /// <summary>The words for each, by kind (A's): a service takes bookings rather than selling out.</summary>
    public static string AvailabilityName(ItemAvailability availability, CatalogKind kind) => (availability, kind) switch
    {
        (ItemAvailability.Hidden, _) => "Inactive",
        (ItemAvailability.Available, CatalogKind.Service) => "Taking bookings",
        (ItemAvailability.SoldOut, CatalogKind.Service) => "Not booking",
        (ItemAvailability.Available, CatalogKind.Digital) => "Available",
        (ItemAvailability.SoldOut, CatalogKind.Digital) => "Unavailable",
        (ItemAvailability.Available, _) => "In stock",
        _ => "Sold out",
    };

    /// <summary>What a customer would miss about an item, in the order to fix it: "No price", "No photo".</summary>
    public static IReadOnlyList<string> Missing(CatalogEntry item)
    {
        var missing = new List<string>();
        if (item.PriceType != PriceType.OnRequest && PriceText(item) == "No price yet") missing.Add("No price");
        if (item.Photos.Count == 0) missing.Add("No photo");
        return missing;
    }

    /// <summary>What the Available switch says for an item.</summary>
    public static string AvailabilityLabel(CatalogEntry item) =>
        SoldOut(item) ? "Sold out"
        : item.IsAvailable ? "Available"
        : item.Kind == CatalogKind.Service ? "Paused"
        : "Not available";

    public static string? PlaceName(ServicePlace place) => place switch
    {
        ServicePlace.AtBusiness => "At your shop or studio",
        ServicePlace.AtCustomer => "At the customer's place",
        ServicePlace.Online => "Online",
        _ => null,
    };

    public static string? DeliveryName(DigitalDelivery delivery) => delivery switch
    {
        DigitalDelivery.LinkOnWhatsApp => "A link, on WhatsApp",
        DigitalDelivery.FileOnWhatsApp => "A file, on WhatsApp",
        DigitalDelivery.Email => "By email",
        _ => null,
    };
}
