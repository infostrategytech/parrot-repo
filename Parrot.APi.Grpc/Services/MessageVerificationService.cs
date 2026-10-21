using System.Security.Claims;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Integrations;
using Parrot.Grpc.Contracts.Messaging.V1;
using Parrot.Application.Workspaces;

namespace Parrot.APi.Grpc.Services;

[Authorize]
public sealed class MessageVerificationGrpcService(
    IMessageRepository repository,
    ILogger<MessageVerificationGrpcService> logger)
    : MessageVerificationService.MessageVerificationServiceBase
{
    public override async Task<CheckForMessageResponse> CheckForMessage(
        CheckForMessageRequest request,
        ServerCallContext context)
    {
        DateTime since = DateTimeOffset.FromUnixTimeSeconds(request.SinceUnixSeconds).UtcDateTime;
        ClaimsPrincipal user = context.GetHttpContext().User;
        // The session's workspace owns the connections; a token from before
        // workspaces has none, and its user id is that tenant's first workspace.
        string? owner = user.FindFirstValue(WorkspaceClaims.WorkspaceId)
            ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(owner))
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Sign in to check for a message."));

        // The setup screens send Parrot's connection id; anything else cannot be one of the caller's.
        if (!Guid.TryParse(request.ChannelId, out Guid connectionId))
        {
            logger.LogWarning("Message check for a channel id that is not a connection id. Platform={Platform}", request.Platform);
            return new CheckForMessageResponse { MessageReceived = false };
        }

        logger.LogInformation(
            "Checking for message on connection {ConnectionId} (platform={Platform}) since {Since}",
            connectionId,
            request.Platform,
            since);

        ReceivedMessageSummary? summary = await repository.FindFirstAsync(
            connectionId,
            owner,
            since,
            context.CancellationToken);

        if (summary is null)
            return new CheckForMessageResponse { MessageReceived = false };

        return new CheckForMessageResponse
        {
            MessageReceived = true,
            MessageId = summary.MessageId,
            FromNumber = summary.From,
            ReceivedAtUnixSeconds = new DateTimeOffset(summary.ReceivedAt, TimeSpan.Zero).ToUnixTimeSeconds(),
        };
    }
}
