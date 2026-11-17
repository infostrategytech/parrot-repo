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

    /// <summary>Cancels the conversation's other open orders (awaiting checkout or payment). Returns them, to let their stock go.</summary>
    Task<IReadOnlyList<PaymentOrder>> CancelOpenAsync(string workspaceId, Guid connectionId, string sender, Guid keep, DateTime at, CancellationToken ct);

    /// <summary>Open orders (awaiting checkout or payment) whose time is up, and orders stuck being made since <paramref name="stuckBefore"/>.</summary>
    Task<IReadOnlyList<PaymentOrder>> ListDueAsync(DateTime now, DateTime stuckBefore, int limit, CancellationToken ct);
}
