namespace Parrot.Web.Features.Dashboard.Catalog;

/// <summary>
/// Where the business's products and services are kept (Concept B of
/// Products &amp; Services). The screens reach items only through this, so
/// connecting the catalog API means implementing this one interface and
/// registering it in Program.cs; no screen changes.
///
/// The catalog API is larger than what an owner sees here: a catalog and a
/// product type per kind, a SKU, prices on offers and stock on availability
/// records. The store hides that; see catalog-backend-requests.
/// </summary>
public interface ICatalogStore
{
    /// <summary>The business's items, drafts too.</summary>
    Task<IReadOnlyList<CatalogEntry>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves a new item (a draft, or live) and returns it with its id.</summary>
    Task<CatalogEntry> CreateAsync(CatalogEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Saves everything about an existing item.</summary>
    Task SaveAsync(CatalogEntry entry, CancellationToken cancellationToken = default);

    /// <summary>The one change made most: available now, or not.</summary>
    Task SetAvailableAsync(string id, bool available, CancellationToken cancellationToken = default);

    /// <summary>Removes it from the list; the team and agents stop offering it.</summary>
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// The business's categories, in the order it shows them (a menu's
    /// sections). A category can exist before anything is in it; one an item
    /// uses but the list doesn't have yet comes last.
    /// </summary>
    Task<IReadOnlyList<string>> ListCategoriesAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves the categories and their order. Moving items between them is done item by item.</summary>
    Task SaveCategoriesAsync(IReadOnlyList<string> categories, CancellationToken cancellationToken = default);
}

/// <summary>
/// What kind of thing it is, asked first: it decides what the rest of the
/// item asks for (research, 9 October 2026: Square, Selar and Amazon all
/// adapt the form to the kind, so a salon never sees stock or weight).
/// </summary>
public enum CatalogKind { Product, Food, Service, Digital }

/// <summary>A draft is saved but not offered to customers yet; live is offered.</summary>
public enum CatalogStatus { Draft, Live }

/// <summary>How the price is set, in plain words. "On request" makes the agent ask the team, never guess.</summary>
public enum PriceType { Fixed, From, OnRequest }

/// <summary>Where a service happens. Optional.</summary>
public enum ServicePlace { NotSaid, AtBusiness, AtCustomer, Online }

/// <summary>How a digital item reaches the customer. Optional.</summary>
public enum DigitalDelivery { NotSaid, LinkOnWhatsApp, FileOnWhatsApp, Email }

/// <summary>A choice that changes what is sold: Size (S, M, L), Colour (Indigo, Rust).</summary>
public sealed class ItemOption
{
    public string Name { get; set; } = "";
    public List<string> Values { get; set; } = [];
    public ItemOption Copy() => new() { Name = Name, Values = [.. Values] };
}

/// <summary>One combination of the options ("M", "M / Indigo"), with its own price and stock.</summary>
public sealed class ItemVariant
{
    public string Key { get; set; } = "";

    /// <summary>Null uses the item's price.</summary>
    public decimal? Price { get; set; }

    public bool IsAvailable { get; set; } = true;

    public int? Stock { get; set; }
    public ItemVariant Copy() => new() { Key = Key, Price = Price, IsAvailable = IsAvailable, Stock = Stock };
}

/// <summary>Unfinished editor values, not approved product information. The API must persist these for drafts.
/// Kept on the frontend contract so a draft can resume without losing partial amounts or unnamed variants.</summary>
public sealed class CatalogEditorDraft
{
    public string Pricing { get; set; } = "single";
    public string? Price { get; set; }
    public string? Stock { get; set; }
    public string Unit { get; set; } = "";
    public List<CatalogVariantDraft> Variants { get; set; } = [];
    public CatalogEditorDraft Copy() => new()
    {
        Pricing = Pricing, Price = Price, Stock = Stock, Unit = Unit,
        Variants = [.. Variants.Select(row => row with { })],
    };
}

public sealed record CatalogVariantDraft(string Name, string? Price, bool SoldOut, string? Stock = null);

/// <summary>Something added to the item at a price: gift wrap, a takeaway pack, an extra fitting.</summary>
public sealed class ItemExtra
{
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public ItemExtra Copy() => new() { Name = Name, Price = Price };
}

/// <summary>A fact as label and value (Material: Cotton): answers precise questions better than prose.</summary>
public sealed class ItemFact
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
    public ItemFact Copy() => new() { Label = Label, Value = Value };
}

/// <summary>A question customers ask about it, with the owner's own answer.</summary>
public sealed class ItemAnswer
{
    public string Question { get; set; } = "";
    public string Answer { get; set; } = "";
    public ItemAnswer Copy() => new() { Question = Question, Answer = Answer };
}

/// <summary>One thing the business sells, as the owner thinks of it.</summary>
public sealed class CatalogEntry
{
    public string Id { get; set; } = "";
    public CatalogKind Kind { get; set; } = CatalogKind.Product;
    public CatalogStatus Status { get; set; } = CatalogStatus.Draft;
    public CatalogEditorDraft? EditorDraft { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>The owner's own grouping ("Clothing", "Bags"). Empty for none.</summary>
    public string Group { get; set; } = "";

    public List<string> Photos { get; set; } = [];

    // ── Price: the live part, read at the moment the agent answers ──────────
    public PriceType PriceType { get; set; } = PriceType.Fixed;

    /// <summary>In naira. Null until set; "on request" has none.</summary>
    public decimal? Price { get; set; }

    /// <summary>Optional: "per yard", "per person", "per hour".</summary>
    public string PriceUnit { get; set; } = "";

    // ── Availability ────────────────────────────────────────────────────────
    /// <summary>The owner's switch: selling it now, or paused.</summary>
    public bool IsAvailable { get; set; } = true;

    /// <summary>Frontend review metadata: unknown is distinct from explicitly unavailable.
    /// False preserves existing confirmed entries. Requires a durable API field before production.</summary>
    public bool AvailabilityIsUnconfirmed { get; set; }

    /// <summary>Products: keep count, so it is out of stock at zero (per option when it has options).</summary>
    public bool TracksStock { get; set; }

    public int? Stock { get; set; }

    /// <summary>Products and food: made after the order, with a lead time.</summary>
    public bool MadeToOrder { get; set; }

    public string LeadTime { get; set; } = "";

    /// <summary>Services: "About 1 week", "3 hours".</summary>
    public string Duration { get; set; } = "";

    public ServicePlace Place { get; set; } = ServicePlace.NotSaid;

    public DigitalDelivery Delivery { get; set; } = DigitalDelivery.NotSaid;

    // ── Options and extras ──────────────────────────────────────────────────
    /// <summary>Up to two (Size, Colour). Their combinations are the variants.</summary>
    public List<ItemOption> Options { get; set; } = [];

    public List<ItemVariant> Variants { get; set; } = [];

    public List<ItemExtra> Extras { get; set; } = [];

    // ── Facts: what is true about it ────────────────────────────────────────
    public List<ItemFact> Facts { get; set; } = [];

    /// <summary>What customers call it: other names, Pidgin, Yoruba, Igbo, Hausa, common misspellings.</summary>
    public List<string> OtherNames { get; set; } = [];

    public List<ItemAnswer> Answers { get; set; } = [];

    /// <summary>Who it suits (and who it doesn't): drives good recommendations.</summary>
    public string ForWho { get; set; } = "";

    /// <summary>Items to offer with it, or instead of it when it is sold out.</summary>
    public List<string> Related { get; set; } = [];

    // ── Instructions: how the agent sells it ────────────────────────────────
    /// <summary>Open to offers; never below <see cref="LowestPrice"/>, which the system enforces, not the agent.</summary>
    public bool Negotiable { get; set; }

    public decimal? LowestPrice { get; set; }

    /// <summary>Things nobody should claim about it.</summary>
    public List<string> NeverClaim { get; set; } = [];

    /// <summary>Owner-authored situations in which the agent should ask a person; API integration pending.</summary>
    public string HumanHandoffNote { get; set; } = "";

    // ── Business policies: inherited unless changed for this item ───────────
    /// <summary>Null uses the business's delivery details.</summary>
    public string? DeliveryNote { get; set; }

    /// <summary>Null uses the business's returns policy.</summary>
    public string? ReturnsNote { get; set; }

    /// <summary>Questions customers asked about it that the agent could not answer.</summary>
    public List<string> Unanswered { get; set; } = [];

    /// <summary>Owner-approved source posts, separate from current price and availability.</summary>
    public List<CatalogSource> Sources { get; set; } = [];
    public DateTimeOffset? PriceConfirmedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool HasOptions => Options.Count > 0 && Variants.Count > 0;

    /// <summary>
    /// Not offered at all, but kept (a seasonal item, one that is coming
    /// back): unlike "not available", which means sold out for now.
    /// </summary>
    public bool IsHidden { get; set; }

    /// <summary>Where the AI assistant is with the latest save; null when the catalog hasn't tracked it yet.</summary>
    public AiProgress? Ai { get; set; }

    /// <summary>Photos the AI rejected or couldn't read, with its reason.</summary>
    public List<AiIssue> AiIssues { get; set; } = [];

    /// <summary>Can a customer have it right now?</summary>
    public bool AvailableNow =>
        !CatalogReadiness.IsDraft(this) && !IsHidden && !AvailabilityIsUnconfirmed && IsAvailable && (HasOptions
            ? Variants.Any(variant => variant.IsAvailable && (!TracksStock || variant.Stock > 0))
            : MadeToOrder || Kind is CatalogKind.Service or CatalogKind.Digital || !TracksStock || Stock > 0);

    /// <summary>The combinations of the options, keeping what was set for any that still exist.</summary>
    public void RebuildVariants()
    {
        var keys = Options.Where(option => option.Values.Count > 0)
            .Aggregate(new List<string> { "" }, (keys, option) =>
                [.. keys.SelectMany(key => option.Values.Select(value => key.Length == 0 ? value : $"{key} / {value}"))])
            .Where(key => key.Length > 0)
            .ToList();
        Variants = [.. keys.Select(key => Variants.FirstOrDefault(variant => variant.Key == key)?.Copy() ?? new ItemVariant { Key = key })];
    }

    public CatalogEntry Copy() => new()
    {
        Sources = [.. Sources], PriceConfirmedAt = PriceConfirmedAt,
        Id = Id, Kind = Kind, Status = Status, EditorDraft = EditorDraft?.Copy(), Name = Name, Description = Description, Group = Group,
        Photos = [.. Photos], PriceType = PriceType, Price = Price, PriceUnit = PriceUnit,
        IsAvailable = IsAvailable, AvailabilityIsUnconfirmed = AvailabilityIsUnconfirmed, IsHidden = IsHidden, TracksStock = TracksStock, Stock = Stock, MadeToOrder = MadeToOrder, LeadTime = LeadTime,
        Duration = Duration, Place = Place, Delivery = Delivery,
        Options = [.. Options.Select(option => option.Copy())], Variants = [.. Variants.Select(variant => variant.Copy())],
        Extras = [.. Extras.Select(extra => extra.Copy())], Facts = [.. Facts.Select(fact => fact.Copy())],
        OtherNames = [.. OtherNames], Answers = [.. Answers.Select(answer => answer.Copy())], ForWho = ForWho,
        Related = [.. Related], Negotiable = Negotiable, LowestPrice = LowestPrice, NeverClaim = [.. NeverClaim], HumanHandoffNote = HumanHandoffNote,
        DeliveryNote = DeliveryNote, ReturnsNote = ReturnsNote, Unanswered = [.. Unanswered], UpdatedAt = UpdatedAt,
        Ai = Ai, AiIssues = [.. AiIssues],
    };
}

/// <summary>
/// Real mode until the catalog API is connected: items last for the session.
/// Concept B shows only in demo mode for now, so this is a safe default.
/// </summary>
public sealed class SessionCatalogStore : ICatalogStore
{
    private readonly List<CatalogEntry> _items = [];
    private readonly List<string> _categories = [];

    public Task<IReadOnlyList<CatalogEntry>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CatalogEntry>>(_items.Select(item => item.Copy()).ToList());

    public Task<CatalogEntry> CreateAsync(CatalogEntry entry, CancellationToken cancellationToken = default)
    {
        var saved = entry.Copy();
        saved.Id = Guid.NewGuid().ToString();
        saved.UpdatedAt = DateTimeOffset.UtcNow;
        _items.Add(saved);
        return Task.FromResult(saved.Copy());
    }

    public Task SaveAsync(CatalogEntry entry, CancellationToken cancellationToken = default)
    {
        var index = _items.FindIndex(item => item.Id == entry.Id);
        if (index < 0) throw new HttpRequestException("No such item.", null, System.Net.HttpStatusCode.NotFound);
        var saved = entry.Copy();
        saved.UpdatedAt = DateTimeOffset.UtcNow;
        _items[index] = saved;
        return Task.CompletedTask;
    }

    public Task SetAvailableAsync(string id, bool available, CancellationToken cancellationToken = default)
    {
        var item = _items.FirstOrDefault(item => item.Id == id)
            ?? throw new HttpRequestException("No such item.", null, System.Net.HttpStatusCode.NotFound);
        item.IsAvailable = available;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        _items.RemoveAll(item => item.Id == id);
        foreach (var item in _items) item.Related.Remove(id);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListCategoriesAsync(CancellationToken cancellationToken = default)
    {
        var used = _items.Select(item => item.Group).Where(group => group.Length > 0)
            .Where(group => !_categories.Contains(group, StringComparer.CurrentCultureIgnoreCase))
            .Distinct(StringComparer.CurrentCultureIgnoreCase);
        return Task.FromResult<IReadOnlyList<string>>([.. _categories, .. used]);
    }

    public Task SaveCategoriesAsync(IReadOnlyList<string> categories, CancellationToken cancellationToken = default)
    {
        _categories.Clear();
        _categories.AddRange(categories.Select(category => category.Trim()).Where(category => category.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase));
        return Task.CompletedTask;
    }

    /// <summary>For a demo store to start with sample items and categories.</summary>
    internal void Seed(IEnumerable<CatalogEntry> items, IEnumerable<string>? categories = null)
    {
        _items.AddRange(items);
        if (categories is not null) _categories.AddRange(categories);
    }
}

/// <summary>Where the AI assistant is with an item's latest save.</summary>
public enum AiProgress
{
    /// <summary>Saved; the AI is still learning the change.</summary>
    Updating,

    /// <summary>The AI has the latest save.</summary>
    Ready,

    /// <summary>The AI rejected a photo; the owner should look at it.</summary>
    NeedsAttention,

    /// <summary>The AI couldn't finish; it retries, nothing for the owner to do.</summary>
    Failed,
}

/// <summary>A photo the AI rejected or couldn't read. PhotoUrl is null for a photo not among the item's own.</summary>
public sealed record AiIssue(string? PhotoUrl, string Status, string? Reason);
