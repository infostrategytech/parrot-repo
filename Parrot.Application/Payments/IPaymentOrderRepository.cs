using Parrot.Domain.Entities;

namespace Parrot.Application.Payments;

public interface IPaymentOrderRepository
{
    Task<PaymentOrder?> FindByIdempotencyKeyAsync(string workspaceId, string idempotencyKey, CancellationToken ct);

    Task<PaymentOrder?> FindAsync(string workspaceId, Guid orderId, CancellationToken ct);

    /// <summary>The order a payment names. References are unique across workspaces, so the payment finds its workspace.</summary>
    Task<PaymentOrder?> FindByReferenceAsync(string reference, CancellationToken ct);

    /// <summary>Saves a new order. False when the workspace already has one with its idempotency key.</summary>
    Task<bool> TryAddAsync(PaymentOrder order, CancellationToken ct);

    /// <summary>Saves the changes to an order already saved.</summary>
    Task UpdateAsync(PaymentOrder order, CancellationToken ct);

    /// <summary>
    /// Saves the order's status and payment only if it's still in <paramref name="expected"/>:
    /// the payment and the sweep can reach the same order at once, and only one may win.
    /// </summary>
    Task<bool> TryUpdateFromAsync(PaymentOrder order, PaymentOrderStatus expected, CancellationToken ct);

    /// <summary>
    /// Moves an order from one status to another, with the fields given, only if it's still in
    /// <paramref name="expected"/> and unpaid. Two presses of Pay at once: only one starts the payment.
    /// </summary>
    Task<bool> TryMoveAsync(PaymentOrder order, PaymentOrderStatus expected, CancellationToken ct);

    /// <summary>
    /// The conversation's cart the customer hasn't pressed Pay on yet and whose time isn't up: the one
    /// create_cart changes rather than replaces. The newest, should there be more than one.
    /// </summary>
    Task<PaymentOrder?> FindOpenCartAsync(string workspaceId, Guid connectionId, string sender, DateTime now, CancellationToken ct);

    /// <summary>The conversation's newest cart, whatever its state; null when it has never had one.</summary>
    Task<PaymentOrder?> FindLatestCartAsync(string workspaceId, Guid connectionId, string sender, CancellationToken ct);

    /// <summary>
    /// Saves a cart's new contents (lines, totals, times, reference, hold, customer details) only if
    /// it's still unpaid, Pay hasn't been pressed and nobody changed it since it was read
    /// (<paramref name="readAt"/> is the UpdatedAt it was read with). False when any of that changed.
    /// </summary>
    Task<bool> TryReplaceCartAsync(PaymentOrder order, DateTime readAt, CancellationToken ct);

    /// <summary>Cancels the conversation's other open orders (awaiting checkout or payment). Returns them, to let their stock go.</summary>
    Task<IReadOnlyList<PaymentOrder>> CancelOpenAsync(string workspaceId, Guid connectionId, string sender, Guid keep, DateTime at, CancellationToken ct);

    /// <summary>Open orders (awaiting checkout or payment) whose time is up, and orders stuck being made since <paramref name="stuckBefore"/>.</summary>
    Task<IReadOnlyList<PaymentOrder>> ListDueAsync(DateTime now, DateTime stuckBefore, int limit, CancellationToken ct);
}
