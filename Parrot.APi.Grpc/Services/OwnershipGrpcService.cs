using System.Security.Claims;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Parrot.Application.Agents;
using Parrot.Application.Auth;
using Parrot.Domain.Exceptions;
using Parrot.Grpc.Contracts.Ownership.V1;

namespace Parrot.APi.Grpc.Services;

/// <summary>
/// Omni-AI asks whether a connection belongs to the workspace it is acting for, before an
/// agent may answer on it. The same check as GET /api/internal/connections/{id}/validate,
/// and like it open only to a service token: a session token never carries agent:service.
/// </summary>
[Authorize(Policy = ServiceScopes.PolicyName)]
public sealed class OwnershipGrpcService(
    IConnectionService connections,
    ILogger<OwnershipGrpcService> logger)
    : OwnershipService.OwnershipServiceBase
{
    public override async Task<VerifyOwnershipResponse> VerifyOwnership(VerifyOwnershipRequest request, ServerCallContext context)
    {
        ClaimsPrincipal caller = context.GetHttpContext().User;
        string workspaceId = caller.FindFirstValue(ServiceScopes.TenantClaim)
            ?? caller.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? caller.FindFirstValue("sub")
            ?? throw new RpcException(new Status(StatusCode.Unauthenticated, "The service token names no workspace."));

        if (!Guid.TryParse(request.ChannelId, out Guid connectionId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "channel_id must be a connection id."));

        // The ids in the request are for checking only; the token decides the workspace.
        if (request.TenantId != workspaceId || request.WorkspaceId != workspaceId)
        {
            logger.LogWarning("Ownership check named a workspace other than the token's. Connection={ConnectionId}", connectionId);
            throw new RpcException(new Status(StatusCode.PermissionDenied, "The request names a different workspace from the token."));
        }

        try
        {
            ConnectionValidationResult owned = await connections.ValidateForWorkspaceAsync(connectionId, workspaceId, context.CancellationToken);
            return new VerifyOwnershipResponse { Owned = true, Platform = owned.ChannelKey };
        }
        catch (NotFoundException)
        {
            // Missing, or another owner's: the same answer, so ids cannot be probed.
            throw new RpcException(new Status(StatusCode.NotFound, "Connection not found."));
        }
        catch (ForbiddenException)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "The connection belongs to another workspace."));
        }
    }
}
