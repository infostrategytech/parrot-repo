using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using Parrot.Application.Messaging;
using Parrot.Application.Realtime;

namespace Parrot.Api.Realtime;

/// <summary>
/// Pushes a persisted message to its recipient's live connections, if they
/// have any. This is best-effort by design: the message is already in
/// MongoDB, so an offline recipient, Redis being down or a failed push
/// changes nothing durable. The client recovers anything it missed from
/// history. Nothing here is ever written to Redis.
///
/// It never throws (other than for shutdown): a throwing handler would make
/// the bus redeliver the same event forever and hold up the partition.
/// </summary>
public sealed class MessagePersistedRouter(
    IOnlineSessionStore sessions,
    IHubContext<MessagingHub> hub,
    ILogger<MessagePersistedRouter> logger)
{
    public async Task RouteAsync(MessagePersisted persisted, CancellationToken cancellationToken)
    {
        StoredMessage message = persisted.Data;
        long started = Stopwatch.GetTimestamp();
        // A templated scope, not a dictionary: the console formatter prints a dictionary as its type name,
        // which left every realtime line without the message it was about.
        using IDisposable? scope = logger.BeginScope(
            "MessageId={MessageId} Source={Source} ConnectionId={ConnectionId} ConversationKey={ConversationKey} Sequence={Sequence} CorrelationId={CorrelationId}",
            message.MessageId, persisted.Source, message.ConnectionId, message.ConversationKey, message.Sequence, persisted.CorrelationId);

        if (string.IsNullOrEmpty(message.OwnerUserId))
        {
            logger.LogWarning("realtime.skipped Outcome=no_recipient");
            return;
        }

        IReadOnlyList<string> connections;
        try
        {
            connections = await sessions.GetConnectionsAsync(message.OwnerUserId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "realtime.skipped Outcome=session_lookup_failed; the client recovers it from history");
            return;
        }

        if (connections.Count == 0)
        {
            // Offline: nothing to do. The message waits in MongoDB, not in Redis. Information,
            // not Debug: when the CRM is open and a message doesn't appear, this line says the
            // API found no live session for the owner (see OwnerUserId), not that delivery failed.
            logger.LogInformation("realtime.skipped Outcome=offline OwnerUserId={OwnerUserId}", message.OwnerUserId);
            return;
        }

        try
        {
            await hub.Clients.Clients(connections).SendAsync(MessagingHub.MessagePersistedMethod, message, cancellationToken);
            logger.LogInformation("realtime.delivered Connections={Connections} DurationMs={DurationMs}",
                connections.Count, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "realtime.push_failed Connections={Connections}; the client recovers it from history", connections.Count);
        }
    }
}
