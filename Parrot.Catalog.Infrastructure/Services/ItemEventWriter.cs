using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Parrot.Catalog.Application.Documents;
using Parrot.Catalog.Application.Events;
using Parrot.Catalog.Application.Services;
using Parrot.Catalog.Application.Tenancy;
using Parrot.Catalog.Domain.Entities;
using Parrot.Catalog.Domain.Enums;
using Parrot.Catalog.Infrastructure.Data;

namespace Parrot.Catalog.Infrastructure.Services;

/// <summary>
/// Writes one whole-item event per product a write touched, in the same transaction as
/// the write. The fine-grained events (ProductUpdated, PriceChanged…) say which row
/// changed; this one says what the item now is, with everything a consumer needs to
/// embed it: the text, the photos' addresses, and whether it should be searchable.
/// Prices and stock are deliberately left out: they change often and are read live.
///
/// Scoped, so it shares the request's DbContext. Writes record the products they touch
/// as they enqueue their events; the outermost transaction writes the item events once,
/// just before it commits, so a save that changes six rows still yields one event.
/// </summary>
public sealed class ItemEventWriter
{
    /// <summary>Enough for any listing; more would only grow the message.</summary>
    public const int MaxPhotos = 10;

    private readonly CatalogDbContext _db;
    private readonly DerivedDataRebuilder _rebuilder;
    private readonly IItemPhotoStorage _photos;
    private readonly TimeProvider _clock;
    private readonly ITenantContext _tenant;
    private readonly HashSet<Guid> _touched = [];

    public ItemEventWriter(CatalogDbContext db, DerivedDataRebuilder rebuilder, IItemPhotoStorage photos, TimeProvider clock, ITenantContext tenant)
    {
        _tenant = tenant;
        _db = db;
        _rebuilder = rebuilder;
        _photos = photos;
        _clock = clock;
    }

    public void Touch(Guid productId) => _touched.Add(productId);

    /// <summary>Forgets what was touched; called when the transaction ends either way.</summary>
    public void Reset() => _touched.Clear();

    /// <summary>Adds one outbox row per touched product to the open transaction. The caller saves.</summary>
    public async Task WriteAsync(CancellationToken ct)
    {
        if (_touched.Count == 0)
        {
            return;
        }

        Guid[] ids = [.. _touched];
        _touched.Clear();

        List<Product> products = await _db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).ToListAsync(ct);
        string? owner = await OwnerAsync(ct);
        DateTime now = _clock.GetUtcNow().UtcDateTime;
        foreach (Product product in products.OrderBy(p => p.Id))
        {
            (string type, object payload) = await BuildAsync(product, ct);
            Guid eventId = Guid.NewGuid();
            await RecordSentAsync(product, eventId, type, now, ct);
            _db.Outbox.Add(new OutboxMessage
            {
                Id = eventId,
                TenantId = product.TenantId,
                OwnerId = owner,
                UserId = UserOf(_tenant),
                AggregateType = AggregateTypes.Product,
                AggregateId = product.Id,
                EventType = type,
                SchemaVersion = 1,
                Payload = Json.Serialize(payload),
                CreatedAt = now,
            });
        }
    }

    private async Task<(string Type, object Payload)> BuildAsync(Product product, CancellationToken ct)
    {
        if (product.DeletedAt is not null || product.Status == ProductStatus.Retired)
        {
            return (CatalogEventTypes.ItemRemoved, new
            {
                product_id = product.Id,
                catalog_id = product.CatalogId,
                sku = product.Sku,
                version = product.Version,
                reason = product.DeletedAt is not null ? "deleted" : "retired",
            });
        }

        ProductType type = await _db.ProductTypes.AsNoTracking().FirstAsync(t => t.Id == product.ProductTypeId, ct);
        IReadOnlyList<string> categoryPath = await _rebuilder.CategoryPathAsync(product.CategoryId, ct);
        RenderedDocument text = await _rebuilder.RenderItemTextAsync(product, categoryPath, ct);

        var variants = await _db.ProductVariants.AsNoTracking()
            .Where(v => v.ProductId == product.Id && v.DeletedAt == null && v.Status == VariantStatus.Active)
            .OrderBy(v => v.SortOrder).ThenBy(v => v.Sku)
            .Select(v => new { v.Id, v.Sku, v.Name, v.Attributes })
            .ToListAsync(ct);

        var photos = await _db.Media.AsNoTracking()
            .Where(m => m.ProductId == product.Id && m.DeletedAt == null && m.Kind == MediaKind.Image)
            .OrderBy(m => m.SortOrder).ThenBy(m => m.CreatedAt)
            .Take(MaxPhotos)
            .Select(m => new { m.Id, m.VariantId, m.S3Key, m.MimeType, m.AltText, m.SortOrder })
            .ToListAsync(ct);

        bool searchable = product.Status == ProductStatus.Published && !product.Hidden;

        return (CatalogEventTypes.ItemChanged, new
        {
            product_id = product.Id,
            catalog_id = product.CatalogId,
            sku = product.Sku,
            version = product.Version,
            status = EnumNames.ToSnake(product.Status),
            hidden = product.Hidden,
            searchable,
            type = new { id = type.Id, key = type.Key, kind = EnumNames.ToSnake(type.Kind) },
            name = product.Name,
            description = product.Description,
            category = product.CategoryId is null ? null : new { id = product.CategoryId, path = categoryPath },
            attributes = Json.Element(product.Attributes),
            variants = variants.Select(v => new { id = v.Id, sku = v.Sku, name = v.Name, attributes = Json.Element(v.Attributes) }),
            duration = product.Duration,
            lead_time = product.LeadTime,
            text = new { rendered = text.Text, content_hash = text.ContentHash },
            photos = photos.Select(m => new
            {
                media_id = m.Id,
                variant_id = m.VariantId,
                url = _photos.UrlFor(m.S3Key),
                mime_type = m.MimeType,
                alt_text = m.AltText,
                sort_order = m.SortOrder,
            }),
            updated_at = product.UpdatedAt,
        });
    }

    /// <summary>
    /// Notes the message as the latest one the AI has to catch up with. An upsert, so two
    /// first saves of the same item at once can't both try to insert; only the "sent"
    /// columns are touched, so a report the relay is recording at the same time survives.
    /// </summary>
    private Task RecordSentAsync(Product product, Guid eventId, string type, DateTime now, CancellationToken ct)
        => _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO catalog.item_ai_status (product_id, tenant_id, sent_event_id, sent_type, sent_at, photos_rejected, photos_failed)
            VALUES ({product.Id}, {product.TenantId}, {eventId}, {type}, {now}, 0, 0)
            ON CONFLICT (product_id) DO UPDATE
                SET sent_event_id = EXCLUDED.sent_event_id, sent_type = EXCLUDED.sent_type, sent_at = EXCLUDED.sent_at
            """,
            ct);

    /// <summary>
    /// The account that owns the workspace, which catalog.items sends as <c>tenant_id</c>:
    /// the token's <c>owner_id</c> claim, else what the workspace's tenant row recorded
    /// from an earlier token. Null when neither knows.
    /// </summary>
    private async Task<string?> OwnerAsync(CancellationToken ct)
    {
        if (_tenant.OwnerId is { } owner)
        {
            return owner;
        }

        Guid workspace = _tenant.TenantId;
        return await _db.Tenants.AsNoTracking().Where(t => t.Id == workspace).Select(t => t.OwnerId).FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Who made the change, for the message's <c>user_id</c>: the <c>sub</c> of the
    /// request's token (a user id for a person; the workspace id for a service token), or
    /// <c>tools</c> for an operator command. Null when no one is known.
    /// </summary>
    public static string? UserOf(ITenantContext tenant) => string.IsNullOrEmpty(tenant.Actor) ? null : tenant.Actor;

    /// <summary>The product a fine-grained event is about: its own id, or the <c>product_id</c> in its payload.</summary>
    public static Guid? ProductOf(string aggregateType, Guid aggregateId, string payloadJson)
    {
        if (aggregateType == AggregateTypes.Product)
        {
            return aggregateId;
        }

        using JsonDocument doc = JsonDocument.Parse(payloadJson);
        return doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("product_id", out JsonElement id)
            && id.ValueKind == JsonValueKind.String
            && Guid.TryParse(id.GetString(), out Guid productId)
                ? productId
                : null;
    }
}
