using Parrot.Application.Payments;

namespace Parrot.Api.Realtime;

/// <summary>
/// Every minute, marks payment orders whose time is up as expired and lets their stock go.
/// Stock doesn't wait on it: a hold stops counting at its time by itself, so a late or
/// missed round only leaves an order labelled pending a little longer.
/// </summary>
public sealed class PaymentExpiryService(IServiceScopeFactory scopes, ILogger<PaymentExpiryService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using PeriodicTimer timer = new(Interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using IServiceScope scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<PaymentConfirmationService>().ExpireDueAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "payments.expiry_failed; trying again next round");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
