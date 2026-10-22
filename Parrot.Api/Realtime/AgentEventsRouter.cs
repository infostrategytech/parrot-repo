using Microsoft.AspNetCore.SignalR;
using Parrot.Application.Agents;
using Parrot.Application.Messaging;
using Parrot.Application.Teams;
using Parrot.Application.Realtime;

namespace Parrot.Api.Realtime;

/// <summary>
/// Keeps each agent event once and tells the owner's live connections about
/// it. The store is the record: an insert that fails is thrown, so the bus
/// redelivers the event once MongoDB is back, and the unique key makes the
/// retry harmless. An event this API cannot read is skipped, not retried,
/// or one bad event would hold up every tenant's partition for ever.
///
/// The push is best effort, exactly as persisted messages are: an offline
/// owner, Redis being down or a failed send changes nothing durable, since
/// the screens read the store the next time they load. Only the first copy
/// of an event is pushed; a redelivery was pushed already.
/// </summary>
public sealed class AgentEventsRouter(
    IAgentActivityStore activity,
    IOnlineSessionStore sessions,
    IHubContext<MessagingHub> hub,
    IRealtimeAudience audience,
    TimeProvider clock,
    ILogger<AgentEventsRouter> logger)
{
    public async Task RouteAsync(AgentEventEnvelope envelope, CancellationToken cancellationToken)
    {
        using IDisposable? scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = envelope.CorrelationId,
            ["EventType"] = envelope.Type,
            ["TurnId"] = envelope.TurnId,
        });

        if (!AgentActivityMapper.TryMap(envelope, clock.GetUtcNow().UtcDateTime, out AgentActivityRecord? record, out string reason))
        {
            if (reason == "unhandled_type")
                logger.LogDebug("agent_activity.skipped Outcome={Outcome}", reason);
            else
                logger.LogWarning("agent_activity.skipped Outcome={Outcome}", reason);
            return;
        }

        bool stored = await activity.InsertAsync(record!, cancellationToken);
        if (!stored)
        {
            logger.LogDebug("agent_activity.duplicate EventKey={EventKey}", record!.EventKey);
            return;
        }

        logger.LogInformation("agent_activity.stored Status={Status} Decision={Decision}", record!.Status, record.Decision);
        await PushAsync(record, cancellationToken);
    }

    private async Task PushAsync(AgentActivityRecord record, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> connections;
        try
        {
            connections = await sessions.GetConnectionsAsync(record.OwnerUserId, cancellationToken);

            // The AI operations screens: for whoever reads the figures or decides approvals,
            // on the channel the conversation is on.
            Domain.Enums.MessagingPlatform? platform = record.ConversationKey is { } key ? ConversationKey.PlatformOf(key) : null;
            connections = await audience.FilterAsync(record.OwnerUserId, connections, a =>
                (a.Can(WorkspacePermissions.AnalyticsDashboard) || a.Can(WorkspacePermissions.ConversationsTakeOver))
                && (platform is { } p ? a.CanReach(p) : a.AllChannels));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "agent_activity.push_skipped Outcome=session_lookup_failed; the screens read the store on their next load");
            return;
        }

        if (connections.Count == 0)
        {
            logger.LogDebug("agent_activity.push_skipped Outcome=offline");
            return;
        }

        try
        {
            await hub.Clients.Clients(connections).SendAsync(MessagingHub.AgentActivityMethod, AgentActivityEvent.From(record), cancellationToken);
            logger.LogInformation("agent_activity.pushed Connections={Connections}", connections.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "agent_activity.push_failed Connections={Connections}; the screens read the store on their next load", connections.Count);
        }
    }
}
