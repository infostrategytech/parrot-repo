using Microsoft.Extensions.Options;
using Parrot.Domain.Entities;
using Parrot.Domain.Exceptions;

namespace Parrot.Application.Payments;

/// <summary>A workspace's checkout settings, as the dashboard shows them. is_default: never saved, so the platform's defaults apply.</summary>
public sealed record PaymentSettingsResponse(
    int CheckoutMinutes,
    CheckoutHoldMode HoldMode,
    int PaymentMinutes,
    PriceChangeRule PriceChangeRule,
    bool IsDefault,
    DateTime? UpdatedAt,
    PaymentSettingsLimits Limits);

/// <summary>The ranges the dashboard may offer.</summary>
public sealed record PaymentSettingsLimits(int MinCheckoutMinutes, int MaxCheckoutMinutes, int MinPaymentMinutes, int MaxPaymentMinutes);

public sealed record UpdatePaymentSettingsRequest(int CheckoutMinutes, CheckoutHoldMode HoldMode, int PaymentMinutes, PriceChangeRule PriceChangeRule);

public interface IWorkspacePaymentSettingsRepository
{
    Task<WorkspacePaymentSettings?> GetAsync(string workspaceId, CancellationToken ct);

    /// <summary>Saves the workspace's row, making it the first time.</summary>
    Task SaveAsync(WorkspacePaymentSettings settings, CancellationToken ct);
}

/// <summary>
/// Each workspace's checkout settings: how long a checkout and its payment link stay open,
/// when stock is kept aside, and what a price change does. Every read and write is for one
/// workspace, named by the caller's session; a workspace that never saved gets the defaults.
/// </summary>
public sealed class WorkspacePaymentSettingsService(IWorkspacePaymentSettingsRepository settings, IOptions<PaymentSettings> defaults, TimeProvider clock)
{
    public const int MinCheckoutMinutes = 5;

    /// <summary>A day: a checkout holds no stock unless the business asks, so it can stay open while the customer decides.</summary>
    public const int MaxCheckoutMinutes = 1440;

    public const int MinPaymentMinutes = 5;

    /// <summary>Two hours: the payment link and the stock held for it shouldn't outlast a customer actually paying.</summary>
    public const int MaxPaymentMinutes = 120;

    public static readonly PaymentSettingsLimits Limits = new(MinCheckoutMinutes, MaxCheckoutMinutes, MinPaymentMinutes, MaxPaymentMinutes);

    public async Task<PaymentSettingsResponse> GetAsync(string workspaceId, CancellationToken ct)
    {
        WorkspacePaymentSettings? saved = await settings.GetAsync(workspaceId, ct);
        WorkspacePaymentSettings effective = saved ?? Defaults(workspaceId);
        return new PaymentSettingsResponse(effective.CheckoutMinutes, effective.HoldMode, effective.PaymentMinutes, effective.PriceChangeRule,
            saved is null, saved?.UpdatedAt, Limits);
    }

    public async Task<PaymentSettingsResponse> UpdateAsync(string workspaceId, string? userId, UpdatePaymentSettingsRequest request, CancellationToken ct)
    {
        List<string> errors = [];
        if (request.CheckoutMinutes is < MinCheckoutMinutes or > MaxCheckoutMinutes)
        {
            errors.Add($"checkoutMinutes must be {MinCheckoutMinutes} to {MaxCheckoutMinutes}.");
        }

        if (request.PaymentMinutes is < MinPaymentMinutes or > MaxPaymentMinutes)
        {
            errors.Add($"paymentMinutes must be {MinPaymentMinutes} to {MaxPaymentMinutes}.");
        }

        if (!Enum.IsDefined(request.HoldMode))
        {
            errors.Add("holdMode must be AtPay, AtCheckout or Never.");
        }

        if (!Enum.IsDefined(request.PriceChangeRule))
        {
            errors.Add("priceChangeRule must be ConfirmNewPrice or Block.");
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        WorkspacePaymentSettings row = new ()
        {
            WorkspaceId = workspaceId,
            CheckoutMinutes = request.CheckoutMinutes,
            HoldMode = request.HoldMode,
            PaymentMinutes = request.PaymentMinutes,
            PriceChangeRule = request.PriceChangeRule,
            UpdatedAt = clock.GetUtcNow().UtcDateTime,
            UpdatedBy = userId,
        };
        await settings.SaveAsync(row, ct);
        return new PaymentSettingsResponse(row.CheckoutMinutes, row.HoldMode, row.PaymentMinutes, row.PriceChangeRule, false, row.UpdatedAt, Limits);
    }

    /// <summary>What applies to the workspace now: its own settings, or the defaults.</summary>
    public async Task<WorkspacePaymentSettings> ResolveAsync(string workspaceId, CancellationToken ct)
        => await settings.GetAsync(workspaceId, ct) ?? Defaults(workspaceId);

    private WorkspacePaymentSettings Defaults(string workspaceId)
    {
        PaymentSettings value = defaults.Value;
        return new WorkspacePaymentSettings
        {
            WorkspaceId = workspaceId,
            CheckoutMinutes = Math.Clamp(value.CheckoutMinutes, MinCheckoutMinutes, MaxCheckoutMinutes),
            HoldMode = value.HoldMode,
            PaymentMinutes = Math.Clamp(value.LinkMinutes, MinPaymentMinutes, MaxPaymentMinutes),
            PriceChangeRule = value.PriceChangeRule,
        };
    }
}
