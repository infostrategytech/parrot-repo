using Parrot.Application.Payments;
using Parrot.Domain.Entities;
using Parrot.Domain.Exceptions;

namespace Parrot.Application.Wallets;

/// <summary>
/// A workspace's wallet as the dashboard shows it, in naira: what the business has now, what has come
/// in and gone out in all, and when it last moved.
/// </summary>
public sealed record WalletResponse(decimal Balance, string Currency, decimal TotalIn, decimal TotalOut, DateTime? UpdatedAt);

/// <summary>
/// One ledger entry, in naira. direction: Credit (money in) or Debit (money out). kind: Payment, Fee, Payout or
/// Adjustment. balanceAfter: the wallet's balance just after it. orderId: the order paid, for a payment.
/// </summary>
public sealed record WalletTransactionResponse(
    Guid Id,
    WalletDirection Direction,
    WalletTransactionKind Kind,
    decimal Amount,
    decimal BalanceAfter,
    string Currency,
    string Description,
    string Reference,
    Guid? OrderId,
    string? ProviderTransactionId,
    DateTime CreatedAt);

/// <summary>A page of the ledger, newest first.</summary>
public sealed record WalletTransactionsPage(IReadOnlyList<WalletTransactionResponse> Items, int Page, int PageSize, int Total);

/// <summary>Reads a workspace's wallet and its ledger. Every read names one workspace: the caller's session's.</summary>
public sealed class WalletService(IWalletRepository wallets)
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    public async Task<WalletResponse> GetAsync(string workspaceId, CancellationToken ct)
    {
        WorkspaceWallet wallet = await wallets.GetOrCreateAsync(workspaceId, ct)
            ?? throw new NotFoundException("This workspace has no wallet.");
        WalletTotals totals = await wallets.TotalsAsync(workspaceId, ct);
        return new WalletResponse(
            CheckoutService.FromMinor(wallet.BalanceMinor),
            wallet.Currency,
            CheckoutService.FromMinor(totals.InMinor),
            CheckoutService.FromMinor(totals.OutMinor),
            wallet.UpdatedAt);
    }

    public async Task<WalletTransactionsPage> ListAsync(string workspaceId, WalletDirection? direction, int page, int pageSize, CancellationToken ct)
    {
        if (direction is { } given && !Enum.IsDefined(given))
        {
            throw new ValidationException("direction must be Credit or Debit.");
        }

        page = Math.Max(page, 1);
        pageSize = pageSize < 1 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize);
        (IReadOnlyList<WalletTransaction> items, int total) = await wallets.ListTransactionsAsync(workspaceId, direction, (page - 1) * pageSize, pageSize, ct);
        return new WalletTransactionsPage([.. items.Select(View)], page, pageSize, total);
    }

    private static WalletTransactionResponse View(WalletTransaction t) => new(
        t.Id,
        t.Direction,
        t.Kind,
        CheckoutService.FromMinor(t.AmountMinor),
        CheckoutService.FromMinor(t.BalanceAfterMinor),
        t.Currency,
        t.Description,
        t.Reference,
        t.PaymentOrderId,
        t.ProviderTransactionId,
        t.CreatedAt);
}
