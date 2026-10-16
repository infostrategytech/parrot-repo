using Parrot.Application.Messaging;

namespace MessageProcessor;

/// <summary>
/// Runs <see cref="PendingSweeper"/> once a minute for the work this process does (media jobs, sends). Several
/// instances may sweep at once: everything the sweeper does is safe to do twice, so a clash costs an extra job at most.
/// A failed sweep is logged and tried again next minute.
/// </summary>
public sealed class PendingSweeperService(PendingSweeper sweeper, WorkerRoles roles, ILogger<PendingSweeperService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Not all at the start: instances restarted together would otherwise sweep together.
        await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(5, 30)), stoppingToken);

        using PeriodicTimer timer = new(Interval);
        do
        {
            try
            {
                if (roles.HasFlag(WorkerRoles.Media)) await sweeper.SweepMediaAsync(DateTime.UtcNow, stoppingToken);
                if (roles.HasFlag(WorkerRoles.Outbound)) await sweeper.SweepSendsAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("sweep.failed Error={Error}; tried again in a minute", ex.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
