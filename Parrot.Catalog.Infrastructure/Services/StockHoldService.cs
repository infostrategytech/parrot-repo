using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Parrot.Catalog.Application.Contracts;
using Parrot.Catalog.Application.Services;
using Parrot.Catalog.Application.Tenancy;
using Parrot.Catalog.Domain.Entities;
using Parrot.Catalog.Domain.Enums;
using Parrot.Catalog.Infrastructure.Data;
using Parrot.Domain.Exceptions;

namespace Parrot.Catalog.Infrastructure.Services;

/// <summary>
/// Stock kept for payment orders. What's left of a counted item is its stock less the holds
/// still held and still in time; a hold past its time stops counting by itself, so stock is
/// never stuck behind a link nobody paid. Placing and committing holds lock each item for
/// the length of the transaction, so two customers can never both get the last one.
/// </summary>
public sealed class StockHoldService : IStockHoldService
{
    public const int MaxLines = 20;
    private const int MaxReference = 64;

    private readonly CatalogDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly TimeProvider _clock;

    public StockHoldService(CatalogDbContext db, ITenantContext tenant, TimeProvider clock)
    {
        _db = db;
        _tenant = tenant;
        _clock = clock;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public async Task<StockHoldsResponse> PlaceAsync(PlaceStockHoldsRequest request, CancellationToken ct)
    {
        string reference = Reference(request.Reference);
        List<StockHoldLine> lines = Lines(request.Lines);
        DateTime expires = DateTime.SpecifyKind(request.ExpiresAt, DateTimeKind.Utc);
        if (expires <= Now || expires > Now.AddDays(2))
        {
            throw new ValidationException("expires_at must be in the future, and within two days.");
        }

        await using IDbContextTransaction tx = await _db.Database.BeginTransactionAsync(ct);
        await LockAsync(lines.Select(l => (l.ProductId, l.VariantId)), ct);

        // Placed already: the same answer, so a retry never holds twice.
        List<StockHold> existing = await _db.StockHolds.Where(h => h.OrderReference == reference).ToListAsync(ct);
        if (existing.Count > 0)
        {
            await tx.CommitAsync(ct);
            return new StockHoldsResponse(reference, lines.Select(l => new HeldLine(l.ProductId, l.VariantId, l.Quantity,
                existing.Any(h => h.ProductId == l.ProductId && h.VariantId == l.VariantId))).ToArray());
        }

        List<HeldLine> result = [];
        List<string> short_ = [];
        foreach (StockHoldLine line in lines)
        {
            Availability? stock = await StockRowAsync(line.ProductId, line.VariantId, ct);
            if (stock is null)
            {
                result.Add(new HeldLine(line.ProductId, line.VariantId, line.Quantity, Held: false));
                continue;
            }

            int left = stock.StockQty!.Value - await HeldAsync(stock, line.ProductId, exceptReference: null, ct);
            if (line.Quantity > left)
            {
                short_.Add($"Only {Math.Max(left, 0)} {await NameAsync(line.ProductId, line.VariantId, ct)} left.");
                continue;
            }

            _db.StockHolds.Add(new StockHold
            {
                Id = Guid.NewGuid(),
                TenantId = _tenant.TenantId,
                OrderReference = reference,
                ProductId = line.ProductId,
                VariantId = line.VariantId,
                Quantity = line.Quantity,
                Status = StockHoldStatus.Held,
                ExpiresAt = expires,
                CreatedAt = Now,
                UpdatedAt = Now,
            });
            result.Add(new HeldLine(line.ProductId, line.VariantId, line.Quantity, Held: true));
        }

        if (short_.Count > 0)
        {
            // Nothing is held unless everything can be.
            throw new ConflictException(string.Join(" ", short_));
        }

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new StockHoldsResponse(reference, result);
    }

    public async Task<CommitStockHoldsResponse> CommitAsync(string reference, CancellationToken ct)
    {
        reference = Reference(reference);
        await using IDbContextTransaction tx = await _db.Database.BeginTransactionAsync(ct);
        List<StockHold> holds = await _db.StockHolds.Where(h => h.OrderReference == reference).ToListAsync(ct);
        await LockAsync(holds.Select(h => (h.ProductId, h.VariantId)), ct);

        List<HeldLine> committed = [];
        List<ShortLine> shortLines = [];
        foreach (StockHold hold in holds)
        {
            if (hold.Status == StockHoldStatus.Committed)
            {
                committed.Add(new HeldLine(hold.ProductId, hold.VariantId, hold.Quantity, Held: true));
                continue;
            }

            Availability? stock = await StockRowAsync(hold.ProductId, hold.VariantId, ct);
            if (stock is null)
            {
                // No longer counted: nothing to take off.
                hold.Status = StockHoldStatus.Committed;
                hold.UpdatedAt = Now;
                committed.Add(new HeldLine(hold.ProductId, hold.VariantId, hold.Quantity, Held: false));
                continue;
            }

            // A hold still in time already kept its stock; one that ran out has to find it again.
            bool inTime = hold.Status == StockHoldStatus.Held && hold.ExpiresAt > Now;
            int left = stock.StockQty!.Value - await HeldAsync(stock, hold.ProductId, exceptReference: reference, ct);
            if (!inTime && hold.Quantity > left)
            {
                hold.Status = StockHoldStatus.Released;
                hold.UpdatedAt = Now;
                shortLines.Add(new ShortLine(hold.ProductId, hold.VariantId, hold.Quantity, Math.Max(left, 0)));
                continue;
            }

            stock.StockQty = Math.Max(0, stock.StockQty.Value - hold.Quantity);
            stock.UpdatedAt = Now;
            hold.Status = StockHoldStatus.Committed;
            hold.UpdatedAt = Now;
            committed.Add(new HeldLine(hold.ProductId, hold.VariantId, hold.Quantity, Held: true));
        }

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new CommitStockHoldsResponse(reference, committed, shortLines);
    }

    public async Task<ReleaseStockHoldsResponse> ReleaseAsync(string reference, CancellationToken ct)
    {
        reference = Reference(reference);
        DateTime now = Now;
        int released = await _db.StockHolds
            .Where(h => h.OrderReference == reference && h.Status == StockHoldStatus.Held)
            .ExecuteUpdateAsync(set => set.SetProperty(h => h.Status, StockHoldStatus.Released).SetProperty(h => h.UpdatedAt, now), ct);
        return new ReleaseStockHoldsResponse(reference, released);
    }

    /// <summary>
    /// The counted stock row a line draws on, or null when its stock isn't counted. An option
    /// with its own count draws on that; one without draws on the product's count, shared by
    /// every option that has none of its own.
    /// </summary>
    private async Task<Availability?> StockRowAsync(Guid productId, Guid? variantId, CancellationToken ct)
    {
        if (variantId is { } variant && await CountedRows().Where(a => a.VariantId == variant).OrderBy(a => a.Region == "*" ? 0 : 1).FirstOrDefaultAsync(ct) is { } own)
        {
            return own;
        }

        return await CountedRows().Where(a => a.ProductId == productId && a.VariantId == null).OrderBy(a => a.Region == "*" ? 0 : 1).FirstOrDefaultAsync(ct);
    }

    private IQueryable<Availability> CountedRows()
    {
        DateTime now = Now;
        return _db.Availability
            .Where(a => a.Status == AvailabilityStatus.Active && a.Mode == AvailabilityMode.InStock && a.StockQty != null)
            .Where(a => a.ValidFrom <= now && (a.ValidUntil == null || a.ValidUntil > now));
    }

    /// <summary>The item's name as the customer knows it: the product, then the option.</summary>
    private async Task<string> NameAsync(Guid productId, Guid? variantId, CancellationToken ct)
    {
        string product = await _db.Products.Where(p => p.Id == productId).Select(p => p.Name).FirstOrDefaultAsync(ct) ?? "of that item";
        string? option = variantId is { } v ? await _db.ProductVariants.Where(x => x.Id == v).Select(x => x.Name).FirstOrDefaultAsync(ct) : null;
        return option is null ? product : $"{product} ({option})";
    }

    /// <summary>How many of a stock row are held for others, by holds still in time.</summary>
    private async Task<int> HeldAsync(Availability stock, Guid productId, string? exceptReference, CancellationToken ct)
    {
        DateTime now = Now;
        IQueryable<StockHold> holds = _db.StockHolds
            .Where(h => h.ProductId == productId && h.Status == StockHoldStatus.Held && h.ExpiresAt > now)
            .Where(h => exceptReference == null || h.OrderReference != exceptReference);
        if (stock.VariantId is { } variant)
        {
            holds = holds.Where(h => h.VariantId == variant);
        }
        else
        {
            // The product's count: holds on the product itself and on options without a count of their own.
            List<Guid?> ownCounts = await CountedRows().Where(a => a.ProductId == productId && a.VariantId != null).Select(a => a.VariantId).Distinct().ToListAsync(ct);
            holds = holds.Where(h => h.VariantId == null || !ownCounts.Contains(h.VariantId));
        }

        return await holds.SumAsync(h => (int?)h.Quantity, ct) ?? 0;
    }

    /// <summary>
    /// One transaction at a time per product (its options can share one count), taken in a
    /// fixed order so two can't wait on each other.
    /// </summary>
    private async Task LockAsync(IEnumerable<(Guid ProductId, Guid? VariantId)> items, CancellationToken ct)
    {
        foreach (string key in items.Select(i => $"stock:{_tenant.TenantId}:{i.ProductId}").Distinct().Order(StringComparer.Ordinal))
        {
            await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({key}))", ct);
        }
    }

    private static string Reference(string? reference)
    {
        string trimmed = reference?.Trim() ?? "";
        return trimmed.Length is > 0 and <= MaxReference
            ? trimmed
            : throw new ValidationException($"reference is required (up to {MaxReference} characters).");
    }

    private static List<StockHoldLine> Lines(IReadOnlyList<StockHoldLine>? lines)
    {
        if (lines is not { Count: > 0 } || lines.Count > MaxLines)
        {
            throw new ValidationException($"lines must hold 1 to {MaxLines} items.");
        }

        if (lines.Any(l => l.ProductId == Guid.Empty || l.VariantId == Guid.Empty || l.Quantity < 1))
        {
            throw new ValidationException("Each line needs a product_id and a quantity of at least 1.");
        }

        return lines.GroupBy(l => (l.ProductId, l.VariantId)).Select(g => new StockHoldLine(g.Key.ProductId, g.Key.VariantId, g.Sum(l => l.Quantity))).ToList();
    }
}
