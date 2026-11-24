using System.Security.Cryptography;
using System.Text;
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
/// Photos (<see cref="AiReplyRequested.Attachments"/>) go after the text, in order, each as its
/// own message under a request id made from the reply's (so a redelivered event never sends one
/// twice). A photo that fails is logged and skipped: it never fails or retries the text. When the
/// text itself wasn't sent, no photos are.
///
/// Permanent failures (missing connection, invalid payload, a send the channel set-up refuses) throw
/// <see cref="UnprocessableMessageException"/> so the event goes to the dead-letter topic
/// rather than being retried indefinitely.
/// </summary>
public sealed class AiReplyProcessor(IServiceScopeFactory scopeFactory, ILogger<AiReplyProcessor> logger)
{
    /// <summary>The most photos one reply sends, as the AI team's contract says.</summary>
    public const int MaxPhotos = 3;

    private const int MaxCaption = 300;

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

        ConversationMessage sent;
        try
        {
            sent = await service.SendAgentReplyAsync(
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
        catch (ConflictException ex)
        {
            // The reply window has closed, the connection is off or has no token, or the request id was used for other
            // text: none of these changes on a retry. Retried, it failed its batch, which was read again for ever and
            // held up every other reply in it. (One "being processed" by another delivery is sent by that one.)
            logger.LogWarning(
                "ai_reply_refused WorkspaceId={WorkspaceId} ConnectionId={ConnectionId} RequestId={RequestId}: {Message}",
                request.WorkspaceId, request.ConnectionId, request.RequestId, ex.Message);
            throw new UnprocessableMessageException("send_refused", ex);
        }

        if (request.Attachments is { Count: > 0 })
        {
            if (sent.Status == "failed")
            {
                logger.LogInformation(
                    "ai_reply_photos_not_sent WorkspaceId={WorkspaceId} ConnectionId={ConnectionId} RequestId={RequestId}: the text wasn't sent",
                    request.WorkspaceId, request.ConnectionId, request.RequestId);
                return;
            }

            await SendPhotosAsync(scope.ServiceProvider, service, request, ct);
        }
    }

    private async Task SendPhotosAsync(IServiceProvider services, ConversationService service, AiReplyRequested request, CancellationToken ct)
    {
        IAgentPhotoFetcher? fetcher = services.GetService<IAgentPhotoFetcher>();
        if (fetcher is null)
        {
            logger.LogWarning("ai_reply_photos_not_sent RequestId={RequestId}: no photo downloader is set up", request.RequestId);
            return;
        }

        List<AiReplyAttachment> photos = request.Attachments!
            .Where(a => !string.IsNullOrWhiteSpace(a?.Url))
            .DistinctBy(a => a.Url.Trim(), StringComparer.Ordinal)
            .Take(MaxPhotos)
            .ToList();
        for (int index = 0; index < photos.Count; index++)
        {
            Guid photoRequest = PhotoRequestId(request.RequestId, index);
            try
            {
                OutgoingFile file = await fetcher.FetchAsync(photos[index].Url.Trim(), ct);
                ConversationMessage sent = await service.SendAgentFileAsync(
                    request.WorkspaceId, request.ConnectionId, request.Recipient, photoRequest, Caption(photos[index].Caption), file, ct);
                if (sent.Status == "failed")
                {
                    logger.LogWarning("ai_reply_photo_failed RequestId={RequestId} Photo={Index}: the channel refused it", request.RequestId, index);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One photo never holds up the others or the text, which has already gone.
                logger.LogWarning("ai_reply_photo_skipped WorkspaceId={WorkspaceId} ConnectionId={ConnectionId} RequestId={RequestId} Photo={Index}: {Reason}",
                    request.WorkspaceId, request.ConnectionId, request.RequestId, index, ex.Message);
            }
        }
    }

    /// <summary>The request id for a reply's nth photo: the same every time, so a resend is recognised.</summary>
    public static Guid PhotoRequestId(Guid replyRequestId, int index)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{replyRequestId:D}:media:{index}"));
        byte[] bytes = hash[..16];
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    /// <summary>One line, at most 300 characters; null when there's nothing to say.</summary>
    private static string? Caption(string? caption)
    {
        if (string.IsNullOrWhiteSpace(caption))
        {
            return null;
        }

        string line = new(caption.Where(c => !char.IsControl(c)).ToArray());
        line = line.Trim();
        return line.Length == 0 ? null : line.Length > MaxCaption ? line[..MaxCaption] : line;
    }
}
