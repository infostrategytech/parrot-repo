using Microsoft.EntityFrameworkCore;
using Npgsql;
using Parrot.Application.Payments;
using Parrot.Domain.Entities;
using Parrot.Infrastructure.Data;

namespace Parrot.Infrastructure.Payments;

public sealed class EfPaymentOrderRepository(IDbContextFactory<ApplicationDbContext> factory) : IPaymentOrderRepository
{
    /// <summary>The unique index on (workspace, idempotency key), named in the model.</summary>
    public const string IdempotencyIndex = "UQ_PaymentOrders_Workspace_IdempotencyKey";

    public async Task<PaymentOrder?> FindByIdempotencyKeyAsync(string workspaceId, string idempotencyKey, CancellationToken ct)
    {
        await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
        return await db.PaymentOrders.AsNoTracking()
            .FirstOrDefaultAsync(o => o.WorkspaceId == workspaceId && o.IdempotencyKey == idempotencyKey, ct);
    }

    public async Task<PaymentOrder?> FindAsync(string workspaceId, Guid orderId, CancellationToken ct)
    {
        await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
        return await db.PaymentOrders.AsNoTracking().FirstOrDefaultAsync(o => o.WorkspaceId == workspaceId && o.Id == orderId, ct);
    }

    public async Task<PaymentOrder?> FindByReferenceAsync(string reference, CancellationToken ct)
    {
        await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
        return await db.PaymentOrders.AsNoTracking().FirstOrDefaultAsync(o => o.Reference == reference, ct);
    }

    public async Task<bool> TryAddAsync(PaymentOrder order, CancellationToken ct)
    {
        await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
        db.PaymentOrders.Add(order);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: IdempotencyIndex })
        {
            return false;
        }
    }

    public async Task UpdateAsync(PaymentOrder order, CancellationToken ct)
    {
        await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
        db.PaymentOrders.Update(order);
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> TryUpdateFromAsync(PaymentOrder order, PaymentOrderStatus expected, CancellationToken ct)
    {
        await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
        int changed = await db.PaymentOrders
            .Where(o => o.Id == order.Id && o.Status == expected && o.PaidAt == null)
            .ExecuteUpdateAsync(set => set
                .SetProperty(o => o.Status, order.Status)
                .SetProperty(o => o.PaidAt, order.PaidAt)
                .SetProperty(o => o.PaidMinor, order.PaidMinor)
                .SetProperty(o => o.ProviderTransactionId, order.ProviderTransactionId)
                .SetProperty(o => o.AttentionReason, order.AttentionReason)
                .SetProperty(o => o.FailureReason, order.FailureReason)
                .SetProperty(o => o.UpdatedAt, order.UpdatedAt), ct);
        return changed == 1;
    }

    public async Task<bool> TryMoveAsync(PaymentOrder order, PaymentOrderStatus expected, CancellationToken ct)
    {
        await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
        List<PaymentOrderLine> lines = [.. order.Lines];
        int changed = await db.PaymentOrders
            .Where(o => o.Id == order.Id && o.WorkspaceId == order.WorkspaceId && o.Status == expected && o.PaidAt == null)
            .ExecuteUpdateAsync(set => set
                .SetProperty(o => o.Status, order.Status)
                .SetProperty(o => o.Lines, lines)
                .SetProperty(o => o.TotalMinor, order.TotalMinor)
                .SetProperty(o => o.ExpiresAt, order.ExpiresAt)
                .SetProperty(o => o.PaymentUrl, order.PaymentUrl)
                .SetProperty(o => o.ProviderSessionId, order.ProviderSessionId)
                .SetProperty(o => o.PaymentStartedAt, order.PaymentStartedAt)
                .SetProperty(o => o.FailureReason, order.FailureReason)
                .SetProperty(o => o.UpdatedAt, order.UpdatedAt), ct);
        return changed == 1;
    }

    public async Task<PaymentOrder?> FindOpenCartAsync(string workspaceId, Guid connectionId, string sender, DateTime now, CancellationToken ct)
    {
        await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
        return await db.PaymentOrders.AsNoTracking()
            .Where(o => o.WorkspaceId == workspaceId && o.ConnectionId == connectionId && o.CustomerSender == sender
                && o.Status == PaymentOrderStatus.AwaitingCheckout && o.PaidAt == null && o.ExpiresAt > now)
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<PaymentOrder?> FindLatestCartAsync(string workspaceId, Guid connectionId, string sender, CancellationToken ct)
    {
        await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
        return await db.PaymentOrders.AsNoTracking()
            .Where(o => o.WorkspaceId == workspaceId && o.ConnectionId == connectionId && o.CustomerSender == sender)
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<bool> TryReplaceCartAsync(PaymentOrder order, DateTime readAt, CancellationToken ct)
    {
        await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
        List<PaymentOrderLine> lines = [.. order.Lines];
        int changed = await db.PaymentOrders
            .Where(o => o.Id == order.Id && o.WorkspaceId == order.WorkspaceId && o.Status == PaymentOrderStatus.AwaitingCheckout
                && o.PaidAt == null && o.UpdatedAt == readAt)
            .ExecuteUpdateAsync(set => set
                .SetProperty(o => o.Lines, lines)
                .SetProperty(o => o.TotalMinor, order.TotalMinor)
                .SetProperty(o => o.QuotedTotalMinor, order.QuotedTotalMinor)
                .SetProperty(o => o.Reference, order.Reference)
                .SetProperty(o => o.HoldMode, order.HoldMode)
                .SetProperty(o => o.AgentId, order.AgentId)
                .SetProperty(o => o.CustomerName, order.CustomerName)
                .SetProperty(o => o.CustomerEmail, order.CustomerEmail)
                .SetProperty(o => o.CustomerPhone, order.CustomerPhone)
                .SetProperty(o => o.Note, order.Note)
                .SetProperty(o => o.CheckoutExpiresAt, order.CheckoutExpiresAt)
                .SetProperty(o => o.ExpiresAt, order.ExpiresAt)
                .SetProperty(o => o.UpdatedAt, order.UpdatedAt), ct);
        return changed == 1;
    }

    public async Task<IReadOnlyList<PaymentOrder>> CancelOpenAsync(string workspaceId, Guid connectionId, string sender, Guid keep, DateTime at, CancellationToken ct)
    {
        await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
        List<PaymentOrder> open = await db.PaymentOrders
            .Where(o => o.WorkspaceId == workspaceId && o.ConnectionId == connectionId && o.CustomerSender == sender
                && o.Id != keep && (o.Status == PaymentOrderStatus.Pending || o.Status == PaymentOrderStatus.AwaitingCheckout) && o.PaidAt == null)
            .ToListAsync(ct);
        List<PaymentOrder> cancelled = [];
        foreach (PaymentOrder order in open)
        {
            // One at a time, and only while still open: a payment may land on any of them.
            PaymentOrderStatus was = order.Status;
            int changed = await db.PaymentOrders
                .Where(o => o.Id == order.Id && o.Status == was && o.PaidAt == null)
                .ExecuteUpdateAsync(set => set.SetProperty(o => o.Status, PaymentOrderStatus.Cancelled).SetProperty(o => o.UpdatedAt, at), ct);
            if (changed == 1)
            {
                order.Status = PaymentOrderStatus.Cancelled;
                cancelled.Add(order);
            }
        }

        return cancelled;
    }

    public async Task<IReadOnlyList<PaymentOrder>> ListDueAsync(DateTime now, DateTime stuckBefore, int limit, CancellationToken ct)
    {
        await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
        return await db.PaymentOrders.AsNoTracking()
            .Where(o => o.PaidAt == null && (((o.Status == PaymentOrderStatus.Pending || o.Status == PaymentOrderStatus.AwaitingCheckout) && o.ExpiresAt <= now)
                || (o.Status == PaymentOrderStatus.Creating && o.UpdatedAt <= stuckBefore)))
            .OrderBy(o => o.ExpiresAt)
            .Take(limit)
            .ToListAsync(ct);
    }
}
