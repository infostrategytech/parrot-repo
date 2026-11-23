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
/// One ledger entry, in naira. type: Credit (money in) or Debit (money out). status: Success, or Failed (on record,
/// but the balance didn't move). kind: Payment, Fee, Payout or Adjustment. actionBy: who made it happen, a user id
/// or a system actor ("system:accelerate"); byName says it in words. balanceAfter: the wallet's balance just after
/// it. orderId: the order paid, for a payment.
/// </summary>
public sealed record WalletTransactionResponse(
    Guid Id,
    WalletTransactionType Type,
    WalletTransactionStatus Status,
    WalletTransactionKind Kind,
    decimal Amount,
    decimal BalanceAfter,
    string Currency,
    string Description,
    string Reference,
    Guid? OrderId,
    string? ProviderTransactionId,
    string ActionBy,
    string ByName,
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

    public async Task<WalletTransactionsPage> ListAsync(string workspaceId, WalletTransactionFilter filter, int page, int pageSize, CancellationToken ct)
    {
        List<string> errors = [];
        if (filter.Type is { } type && !Enum.IsDefined(type))
        {
            errors.Add("type must be Credit or Debit.");
        }

        if (filter.Status is { } status && !Enum.IsDefined(status))
        {
            errors.Add("status must be Success or Failed.");
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        page = Math.Max(page, 1);
        pageSize = pageSize < 1 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize);
        (IReadOnlyList<WalletTransaction> items, int total) = await wallets.ListTransactionsAsync(workspaceId, filter, (page - 1) * pageSize, pageSize, ct);
        return new WalletTransactionsPage([.. items.Select(View)], page, pageSize, total);
    }

    private static WalletTransactionResponse View(WalletTransaction t) => new(
        t.Id,
        t.Type,
        t.Status,
        t.Kind,
        CheckoutService.FromMinor(t.AmountMinor),
        CheckoutService.FromMinor(t.BalanceAfterMinor),
        t.Currency,
        t.Description,
        t.Reference,
        t.PaymentOrderId,
        t.ProviderTransactionId,
        t.ActionBy,
        ByName(t.ActionBy),
        t.CreatedAt);

    /// <summary>Who made an entry, in words: the system actors by name, and a person as "A team member" until people make entries.</summary>
    public static string ByName(string actionBy) => actionBy switch
    {
        WalletTransaction.AccelerateActor => "Accelerate (payment received)",
        _ when WalletTransaction.IsSystemActor(actionBy) => "yára",
        _ => "A team member",
    };
}
