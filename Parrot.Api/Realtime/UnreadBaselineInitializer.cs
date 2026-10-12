using Parrot.Application.Conversations;

namespace Parrot.Api.Realtime;

/// <summary>
/// Creates the read store when the API starts. Its constructor saves the unread baseline
/// the first time it runs, so doing it here ties the baseline to the deploy rather than
/// to whenever someone first opens the inbox. If MongoDB is down the store is created on
/// first use instead: the API must still start.
/// </summary>
public sealed class UnreadBaselineInitializer(IServiceProvider services, ILogger<UnreadBaselineInitializer> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try { _ = services.GetRequiredService<IConversationReadStore>(); }
        catch (Exception ex) { logger.LogWarning(ex, "unread.baseline_deferred; the read store starts on first use"); }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
