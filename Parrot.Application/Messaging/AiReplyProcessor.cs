using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Parrot.Application.Conversations;
using Parrot.Domain.Exceptions;

namespace Parrot.Application.Messaging;

/// <summary>
/// Processes an <see cref="AiReplyRequested"/> event: validates the fields, resolves a
/// <see cref="ConversationService"/> in a short-lived scope, and calls
/// <see cref="ConversationService.SendAgentReplyAsync"/> which sends the reply and
/// publishes <see cref="MessagePersisted"/> with <c>CommsType = AgentToUser</c>.
///
/// Permanent failures (missing connection, invalid payload) throw
/// <see cref="UnprocessableMessageException"/> so the event goes to the dead-letter topic
/// rather than being retried indefinitely.
/// </summary>
public sealed class AiReplyProcessor(IServiceScopeFactory scopeFactory, ILogger<AiReplyProcessor> logger)
{
    public async Task ProcessAsync(AiReplyRequested request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.WorkspaceId)
            || request.ConnectionId == Guid.Empty
            || string.IsNullOrWhiteSpace(request.Recipient)
            || request.RequestId == Guid.Empty
            || string.IsNullOrWhiteSpace(request.Text))
        {
            throw new UnprocessableMessageException("ai_reply_missing_fields");
        }

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        ConversationService service = scope.ServiceProvider.GetRequiredService<ConversationService>();

        try
        {
            await service.SendAgentReplyAsync(
                request.WorkspaceId,
                request.ConnectionId,
                request.Recipient,
                new SendReplyRequest(request.RequestId, request.Text),
                ct);
        }
        catch (NotFoundException ex)
        {
            logger.LogWarning(
                "ai_reply_not_found WorkspaceId={WorkspaceId} ConnectionId={ConnectionId}: {Message}",
                request.WorkspaceId, request.ConnectionId, ex.Message);
            throw new UnprocessableMessageException("connection_not_found", ex);
        }
        catch (ValidationException ex)
        {
            logger.LogWarning(
                "ai_reply_validation_failed WorkspaceId={WorkspaceId} ConnectionId={ConnectionId}: {Message}",
                request.WorkspaceId, request.ConnectionId, ex.Message);
            throw new UnprocessableMessageException("validation_failed", ex);
        }
    }
}
