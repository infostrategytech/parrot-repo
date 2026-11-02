using System.Security.Claims;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Parrot.Application.Auth;
using Parrot.Application.Teams;
using Parrot.Application.Workspaces;
using Parrot.Grpc.Contracts.Identity.V1;
using GrpcWorkspaceAccess = Parrot.Grpc.Contracts.Identity.V1.WorkspaceAccess;

namespace Parrot.APi.Grpc.Services;

/// <summary>
/// Omni-AI forwards a person's own token and asks whether they may still act in the
/// workspace it names, and what they may do there now. The same answer as
/// GET /api/workspaces/current/access: read from the live grant, not the token's copy.
/// </summary>
[Authorize]
public sealed class UserValidationGrpcService(
    IWorkspaceAccessService access,
    ILogger<UserValidationGrpcService> logger)
    : UserValidationService.UserValidationServiceBase
{
    private static readonly ValidateUserResponse Invalid = new() { Valid = false };

    public override async Task<ValidateUserResponse> ValidateUser(ValidateUserRequest request, ServerCallContext context)
    {
        ClaimsPrincipal caller = context.GetHttpContext().User;

        // A service token acts for a workspace, not as a person in it, so it has no access to report.
        if (caller.HasClaim(c => c.Type == ServiceScopes.ServiceClaim))
            throw new RpcException(new Status(StatusCode.Unauthenticated, "ValidateUser takes the person's own token."));

        string? userId = caller.FindFirstValue(ClaimTypes.NameIdentifier) ?? caller.FindFirstValue("sub");
        // A token from before workspaces names none; its user id is the tenant's first workspace.
        string? workspaceId = caller.FindFirstValue(WorkspaceClaims.WorkspaceId) ?? userId;
        string? tenantId = caller.FindFirstValue(ServiceScopes.TenantClaim);
        if (userId is null || workspaceId is null)
            return Invalid;

        // Parrot answers about the token it received and nobody else.
        if (request.UserId != userId || request.WorkspaceId != workspaceId || (tenantId is not null && request.TenantId != tenantId))
        {
            logger.LogWarning("ValidateUser named someone other than the token's holder.");
            return Invalid;
        }

        Parrot.Application.Teams.WorkspaceAccess? found = await access.GetAsync(userId, workspaceId);
        if (found is null)
            return Invalid;

        GrpcWorkspaceAccess shaped = new() { Role = found.Role };
        shaped.Permissions.AddRange(WorkspacePermissions.Effective(found));
        // Empty means every channel, which is what the owner and an unlimited grant reach.
        shaped.Channels.AddRange(WorkspaceChannels.All.Select(c => c.Key).Where(found.Channels.Contains));
        return new ValidateUserResponse { Valid = true, Access = shaped };
    }
}
