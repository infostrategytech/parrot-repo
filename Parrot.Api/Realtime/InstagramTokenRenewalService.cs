using Parrot.Application.Integrations;

namespace Parrot.Api.Realtime;

/// <summary>
/// Runs <see cref="InstagramTokenRenewer"/> every few hours, so Instagram tokens are renewed long
/// before their 60 days run out. A missed round costs nothing: a token is renewed at 30 days old.
/// </summary>
/// <remarks>
/// With several API instances each would renew the same tokens; that is harmless (renewing twice
/// just gives another 60 days twice) and the save only replaces the token it renewed.
/// </remarks>
public sealed class InstagramTokenRenewalService(IServiceScopeFactory scopes, ILogger<InstagramTokenRenewalService> logger) : BackgroundService
{
    private static readonly TimeSpan FirstRun = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstRun, stoppingToken);
            using PeriodicTimer timer = new(Interval);
            do
            {
                try
                {
                    using IServiceScope scope = scopes.CreateScope();
                    InstagramTokenRenewer.Result result = await scope.ServiceProvider.GetRequiredService<InstagramTokenRenewer>()
                        .RenewDueAsync(DateTime.UtcNow, stoppingToken);
                    if (result.Renewed + result.Rejected + result.Failed > 0)
                        logger.LogInformation("instagram.token_renewal Renewed={Renewed} Rejected={Rejected} Failed={Failed}",
                            result.Renewed, result.Rejected, result.Failed);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The database being down is not a reason to stop renewing for good.
                    logger.LogWarning(ex, "instagram.token_renewal_failed; trying again next round");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
