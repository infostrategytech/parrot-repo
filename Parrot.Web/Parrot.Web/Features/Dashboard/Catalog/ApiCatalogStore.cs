using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Web.Features.Auth.State;
using Parrot.Web.Features.Dashboard.Catalog.ConceptE;
using Parrot.Web.Services;

namespace Parrot.Web.Features.Dashboard.Catalog;

/// <summary>
/// Products &amp; Services kept by the catalog service (/api/catalog/items), called as the
/// signed-in person with their session token. The service holds what the form asks for:
/// the item, its prices (one, per variant, or on request), availability, a service's
/// length, a wait in words, photos (uploaded first, then saved with the item), hidden,
/// and a draft's unfinished values. What only the older concepts edit (facts, answers,
/// extras, related items and the like) isn't kept by the service.
/// <para>Categories are created as they're needed. The service doesn't keep their order
/// or delete them yet, so the order shown is the service's.</para>
/// </summary>
public sealed class ApiCatalogStore(HttpClient http, AuthState auth) : ICatalogStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    // The service's largest page.
    private const int PageSize = 100;

    // What the screens don't carry but the service needs back: a photo's key by its address,
    // and each item's variants' ids by name, so saving keeps them rather than making new ones.
    private readonly Dictionary<string, string> _photoKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, Guid>> _variantIds = new(StringComparer.Ordinal);
    private Dictionary<string, Guid>? _categoryIds;

    public async Task<IReadOnlyList<CatalogEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        List<CatalogEntry> items = [];
        for (int page = 1; ; page++)
        {
            ItemList list = await SendAsync<ItemList>(HttpMethod.Get, $"api/catalog/items?page={page}&page_size={PageSize}", null, cancellationToken);
            items.AddRange(list.Items.Select(ToEntry));
            if (list.Items.Count == 0 || page * list.PageSize >= list.Total) break;
        }

        // The screens want oldest first, as they were added.
        items.Reverse();
        return items;
    }

    public async Task<CatalogEntry> CreateAsync(CatalogEntry entry, CancellationToken cancellationToken = default)
    {
        SaveItem request = await ToRequestAsync(entry, cancellationToken);
        return ToEntry(await SendAsync<Item>(HttpMethod.Post, "api/catalog/items", request, cancellationToken));
    }

    public async Task SaveAsync(CatalogEntry entry, CancellationToken cancellationToken = default)
    {
        SaveItem request = await ToRequestAsync(entry, cancellationToken);
        ToEntry(await SendAsync<Item>(HttpMethod.Put, $"api/catalog/items/{Uri.EscapeDataString(entry.Id)}", request, cancellationToken));
    }

    public async Task SetAvailableAsync(string id, bool available, CancellationToken cancellationToken = default)
    {
        Item item = await SendAsync<Item>(HttpMethod.Get, $"api/catalog/items/{Uri.EscapeDataString(id)}", null, cancellationToken);
        AvailabilityInput change = item.Availability is { } now
            ? new AvailabilityInput(now.Mode, available, now.Mode == "in_stock" ? now.Quantity : null, now.Mode == "made_to_order" ? now.LeadTimeDays : null)
            : new AvailabilityInput(ModeOf(ToEntry(item)), available, null, null);
        ToEntry(await SendAsync<Item>(HttpMethod.Patch, $"api/catalog/items/{Uri.EscapeDataString(id)}/availability", change, cancellationToken));
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendRawAsync(HttpMethod.Delete, $"api/catalog/items/{Uri.EscapeDataString(id)}", null, cancellationToken);
        _variantIds.Remove(id);
    }

    public async Task<IReadOnlyList<string>> ListCategoriesAsync(CancellationToken cancellationToken = default)
        => [.. (await CategoriesAsync(cancellationToken)).Keys];

    public async Task SaveCategoriesAsync(IReadOnlyList<string> categories, CancellationToken cancellationToken = default)
    {
        foreach (string category in categories.Select(c => c.Trim()).Where(c => c.Length > 0))
        {
            await CategoryIdAsync(category, cancellationToken);
        }
    }

    // ---- reading ------------------------------------------------------------

    private CatalogEntry ToEntry(Item item)
    {
        string id = item.Id.ToString();
        if (item.Category is { } category && _categoryIds is not null) _categoryIds.TryAdd(category.Name, category.Id);
        foreach (Photo photo in item.Photos ?? []) _photoKeys[photo.Url] = photo.Key;
        _variantIds[id] = item.Pricing.Variants.GroupBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

        CatalogEntry entry = new()
        {
            Id = id,
            Kind = item.Kind switch { "service" => CatalogKind.Service, "food" => CatalogKind.Food, "digital" => CatalogKind.Digital, _ => CatalogKind.Product },
            Status = item.Status == "published" ? CatalogStatus.Live : CatalogStatus.Draft,
            Name = item.Name,
            Description = item.Description ?? string.Empty,
            Group = item.Category?.Name ?? string.Empty,
            Photos = [.. (item.Photos ?? []).Select(p => p.Url)],
            Duration = item.Duration ?? string.Empty,
            LeadTime = item.LeadTime ?? (item.Availability?.LeadTimeDays is { } days ? $"{days} {(days == 1 ? "day" : "days")}" : string.Empty),
            IsHidden = item.Hidden,
            Ai = item.Ai?.State switch
            {
                "updating" => AiProgress.Updating,
                "ready" => AiProgress.Ready,
                "needs_attention" => AiProgress.NeedsAttention,
                "failed" => AiProgress.Failed,
                "not_listed" => AiProgress.NotListed,
                null => null,
                _ => AiProgress.NeedsAttention,
            },
            AiPhotos = [.. (item.Ai?.Photos ?? []).Select(p => new AiPhoto(
                (item.Photos ?? []).FirstOrDefault(own => own.Key == p.PhotoKey)?.Url, p.Status, p.Caption, p.Reason))],
            AiTextReason = item.Ai?.TextReason,
            AiReportedAt = item.Ai?.ReportedAt is { } reportedAt ? new DateTimeOffset(DateTime.SpecifyKind(reportedAt, DateTimeKind.Utc)) : null,
            UpdatedAt = new DateTimeOffset(DateTime.SpecifyKind(item.UpdatedAt, DateTimeKind.Utc)),
        };

        Pricing pricing = item.Pricing;
        entry.CurrencyCode = pricing.Currency;
        string unit = pricing.Per is { Length: > 0 } per ? $"per {per}" : string.Empty;
        switch (pricing.Mode)
        {
            case "on_request":
                entry.PriceType = PriceType.OnRequest;
                break;
            case "variants":
                entry.PriceUnit = unit;
                entry.Options = [new ItemOption { Name = PriceWords.VariantList, Values = [.. pricing.Variants.Select(v => v.Name)] }];
                entry.Variants = [.. pricing.Variants.Select(v => new ItemVariant { Key = v.Name, Price = v.Amount, IsAvailable = v.Available })];
                break;
            default:
                entry.Price = pricing.Amount;
                entry.PriceUnit = unit;
                break;
        }

        if (item.Availability is { } availability)
        {
            // Hidden is off while hidden; shown again, it's on sale.
            entry.IsAvailable = item.Hidden || availability.Available;
            entry.MadeToOrder = availability.Mode == "made_to_order";
            entry.TracksStock = availability.Mode == "in_stock" && availability.Quantity is not null && !entry.HasOptions;
            entry.Stock = entry.TracksStock ? availability.Quantity : null;
        }
        else
        {
            entry.AvailabilityIsUnconfirmed = true;
        }

        if (entry.Status == CatalogStatus.Draft && item.FormDraft is { ValueKind: JsonValueKind.Object } draft)
        {
            try { entry.EditorDraft = draft.Deserialize<CatalogEditorDraft>(DraftJson); }
            catch (JsonException) { }
        }

        return entry;
    }

    // ---- writing ------------------------------------------------------------

    // The draft is the screens' own shape, kept as they write it.
    private static readonly JsonSerializerOptions DraftJson = new(JsonSerializerDefaults.Web);

    private async Task<SaveItem> ToRequestAsync(CatalogEntry entry, CancellationToken ct)
    {
        bool live = entry.Status == CatalogStatus.Live;
        string? per = Per(entry.PriceUnit);
        PricingInput pricing;
        if (entry.PriceType == PriceType.OnRequest)
        {
            pricing = new PricingInput("on_request", null, null, null);
        }
        else if (entry.HasOptions)
        {
            Dictionary<string, Guid> known = _variantIds.GetValueOrDefault(entry.Id) ?? [];
            pricing = new PricingInput("variants", null, per, [.. entry.Variants.Select(v => new VariantInput(
                v.Key,
                v.Price ?? entry.Price,
                known.TryGetValue(v.Key, out Guid variantId) ? variantId : null,
                v.IsAvailable && (!entry.TracksStock || v.Stock is not 0)))]);
        }
        else
        {
            pricing = new PricingInput("single", entry.Price, per, null);
        }
        // This field already exists in the catalog service's ItemPricingInput; omitting it used the catalog default.
        pricing = pricing with { Currency = Parrot.SharedComponents.DesignSystem.CurrencyAmount.Code(entry.CurrencyCode) };

        AvailabilityInput? availability = entry.AvailabilityIsUnconfirmed && !live
            ? null
            : new AvailabilityInput(
                ModeOf(entry),
                entry.IsAvailable,
                ModeOf(entry) == "in_stock" && entry.TracksStock && !entry.HasOptions ? entry.Stock : null,
                null);

        List<string> photos = [];
        foreach (string photo in entry.Photos)
        {
            if (await PhotoKeyAsync(photo, ct) is { } key) photos.Add(key);
        }

        return new SaveItem(
            entry.Name.Trim(),
            KindOf(entry.Kind),
            pricing,
            availability,
            entry.Group.Trim().Length > 0 ? await CategoryIdAsync(entry.Group.Trim(), ct) : null,
            entry.Description.Trim().Length > 0 ? entry.Description.Trim() : null,
            live ? "published" : "draft",
            entry.Kind == CatalogKind.Service && entry.Duration.Trim().Length > 0 ? entry.Duration.Trim() : null,
            entry.MadeToOrder && entry.LeadTime.Trim().Length > 0 ? entry.LeadTime.Trim() : null,
            photos,
            !live && entry.EditorDraft is { } draft ? JsonSerializer.SerializeToElement(draft, DraftJson) : null,
            entry.IsHidden);
    }

    private static string KindOf(CatalogKind kind) => kind switch
    {
        CatalogKind.Service => "service",
        CatalogKind.Food => "food",
        CatalogKind.Digital => "digital",
        _ => "product",
    };

    private static string ModeOf(CatalogEntry entry) => entry.Kind == CatalogKind.Service ? "bookings" : entry.MadeToOrder ? "made_to_order" : "in_stock";

    // "per yard" as the screens write it; the service keeps "yard".
    private static string? Per(string unit)
    {
        string word = unit.Trim();
        if (word.StartsWith("per ", StringComparison.OrdinalIgnoreCase)) word = word[4..].Trim();
        return word.Length > 0 ? word : null;
    }

    /// <summary>A photo as the service keeps it: one just picked (a data: address) is uploaded first.</summary>
    private async Task<string?> PhotoKeyAsync(string photo, CancellationToken ct)
    {
        if (_photoKeys.TryGetValue(photo, out string? key)) return key;
        if (!photo.StartsWith("data:", StringComparison.Ordinal)) return null;

        int comma = photo.IndexOf(',');
        string header = comma > 5 ? photo[5..comma] : string.Empty;
        if (comma < 0 || !header.EndsWith(";base64", StringComparison.Ordinal)) return null;
        string contentType = header[..^";base64".Length];
        byte[] bytes = Convert.FromBase64String(photo[(comma + 1)..]);

        using ByteArrayContent file = new(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using MultipartFormDataContent form = new() { { file, "file", "photo" } };
        Photo uploaded = await SendAsync<Photo>(HttpMethod.Post, "api/catalog/items/photos", form, ct);
        _photoKeys[photo] = uploaded.Key;
        _photoKeys[uploaded.Url] = uploaded.Key;
        return uploaded.Key;
    }

    private async Task<Dictionary<string, Guid>> CategoriesAsync(CancellationToken ct)
    {
        List<Category> categories = await SendAsync<List<Category>>(HttpMethod.Get, "api/catalog/categories", null, ct);
        _categoryIds = categories.GroupBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.CurrentCultureIgnoreCase);
        return _categoryIds;
    }

    private async Task<Guid> CategoryIdAsync(string name, CancellationToken ct)
    {
        Dictionary<string, Guid> known = _categoryIds ?? await CategoriesAsync(ct);
        if (known.TryGetValue(name, out Guid id)) return id;
        try
        {
            Category created = await SendAsync<Category>(HttpMethod.Post, "api/catalog/categories", new { name }, ct);
            known[created.Name] = created.Id;
            return created.Id;
        }
        catch (ApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // Made meanwhile (another tab, a teammate): use that one.
            if ((await CategoriesAsync(ct)).TryGetValue(name, out Guid existing)) return existing;
            throw;
        }
    }

    // ---- calls --------------------------------------------------------------

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using HttpResponseMessage response = await SendRawAsync(method, path, body, ct);
        return await response.Content.ReadFromJsonAsync<T>(Json, ct)
            ?? throw new HttpRequestException("The catalog returned an empty response.");
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        string token = auth.IsAuthenticated ? auth.CurrentUser!.Token : throw new InvalidOperationException("Sign in to continue.");
        using HttpRequestMessage request = new(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = body switch
        {
            null => null,
            HttpContent content => content,
            _ => JsonContent.Create(body, body.GetType(), options: Json),
        };
        HttpResponseMessage response = await http.SendAsync(request, ct);
        try
        {
            await response.EnsureApiSuccessAsync();
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    // ---- the service's shapes ----------------------------------------------

    private sealed record ItemList(List<Item> Items, int Total, int Page, int PageSize);

    private sealed record Item(
        Guid Id,
        string Name,
        string Kind,
        Category? Category,
        string? Description,
        string Status,
        Pricing Pricing,
        Availability? Availability,
        DateTime UpdatedAt,
        string? Duration,
        string? LeadTime,
        List<Photo>? Photos,
        JsonElement? FormDraft,
        bool Hidden,
        Ai? Ai = null);

    private sealed record Ai(string State, DateTime? ReportedAt, string? TextReason, List<AiPhotoBody>? Photos);

    private sealed record AiPhotoBody(string? PhotoKey, string Status, string? Caption, string? Reason);

    private sealed record Category(Guid Id, string Name);

    private sealed record Pricing(string? Mode, decimal? Amount, string? Per, List<Variant> Variants, string? Currency = null);

    private sealed record Variant(Guid Id, string Name, decimal? Amount, bool Available = true);

    private sealed record Availability(string Mode, bool Available, int? Quantity, int? LeadTimeDays);

    private sealed record Photo(string Key, string Url);

    private sealed record SaveItem(
        string Name,
        string Kind,
        PricingInput Pricing,
        AvailabilityInput? Availability,
        Guid? CategoryId,
        string? Description,
        string Status,
        string? Duration,
        string? LeadTime,
        List<string> Photos,
        JsonElement? FormDraft,
        bool Hidden);

    private sealed record PricingInput(string Mode, decimal? Amount, string? Per, List<VariantInput>? Variants, string? Currency = null);

    private sealed record VariantInput(string Name, decimal? Amount, Guid? Id, bool Available);

    private sealed record AvailabilityInput(string Mode, bool Available, int? Quantity, int? LeadTimeDays);
}
