using System.Reflection;
using System.Security.Claims;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Parrot.Application.Agents;
using Parrot.Application.Auth;
using Parrot.Application.Teams;
using Parrot.APi.Grpc.Services;
using Parrot.Domain.Entities;
using Parrot.Domain.Exceptions;
using Parrot.Grpc.Contracts.Identity.V1;
using Parrot.Grpc.Contracts.Ownership.V1;
using WorkspaceAccess = Parrot.Application.Teams.WorkspaceAccess;

namespace Test.Parrot.Api.Tests.Grpc;

// The two calls Omni-AI makes to Parrot over gRPC, answered as its contract
// (pkg/infra/parrot/proto/*.proto) says, from the same logic as the REST routes.
public class OmniValidationGrpcTests
{
    private const string WorkspaceId = "ws-1";
    private const string UserId = "user-1";
    private static readonly Guid Connection = Guid.NewGuid();

    // ---- VerifyOwnership ------------------------------------------------------

    [Fact]
    public void VerifyOwnership_IsOpenOnlyToServiceTokens()
    {
        AuthorizeAttribute attribute = typeof(OwnershipGrpcService).GetCustomAttribute<AuthorizeAttribute>()!;
        Assert.Equal(ServiceScopes.PolicyName, attribute.Policy);
    }

    [Fact]
    public async Task VerifyOwnership_OwnedConnection_ReturnsItsPlatform()
    {
        Mock<IConnectionService> connections = new();
        connections.Setup(c => c.ValidateForWorkspaceAsync(Connection, WorkspaceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectionValidationResult(Connection, "instagram", WorkspaceId));

        VerifyOwnershipResponse response = await Ownership(connections).VerifyOwnership(OwnershipRequest(), Call(ServiceToken()));

        Assert.True(response.Owned);
        Assert.Equal("instagram", response.Platform);
    }

    [Fact]
    public async Task VerifyOwnership_MissingOrAnotherOwners_IsNotFound()
    {
        Mock<IConnectionService> connections = new();
        connections.Setup(c => c.ValidateForWorkspaceAsync(Connection, WorkspaceId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotFoundException("Connection not found."));

        RpcException refused = await Assert.ThrowsAsync<RpcException>(() => Ownership(connections).VerifyOwnership(OwnershipRequest(), Call(ServiceToken())));
        Assert.Equal(StatusCode.NotFound, refused.StatusCode);
    }

    [Fact]
    public async Task VerifyOwnership_SameOwnersOtherWorkspace_IsPermissionDenied()
    {
        Mock<IConnectionService> connections = new();
        connections.Setup(c => c.ValidateForWorkspaceAsync(Connection, WorkspaceId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ForbiddenException("You do not have access to this connection."));

        RpcException refused = await Assert.ThrowsAsync<RpcException>(() => Ownership(connections).VerifyOwnership(OwnershipRequest(), Call(ServiceToken())));
        Assert.Equal(StatusCode.PermissionDenied, refused.StatusCode);
    }

    [Fact]
    public async Task VerifyOwnership_WorkspaceOtherThanTheTokens_IsRefusedWithoutLookingItUp()
    {
        Mock<IConnectionService> connections = new();

        RpcException refused = await Assert.ThrowsAsync<RpcException>(() =>
            Ownership(connections).VerifyOwnership(OwnershipRequest(workspace: "ws-2"), Call(ServiceToken())));

        Assert.Equal(StatusCode.PermissionDenied, refused.StatusCode);
        connections.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task VerifyOwnership_MalformedId_IsInvalidArgument()
    {
        VerifyOwnershipRequest request = OwnershipRequest();
        request.ChannelId = "not-a-guid";

        RpcException refused = await Assert.ThrowsAsync<RpcException>(() => Ownership(new Mock<IConnectionService>()).VerifyOwnership(request, Call(ServiceToken())));
        Assert.Equal(StatusCode.InvalidArgument, refused.StatusCode);
    }

    // ---- ValidateUser ---------------------------------------------------------

    [Fact]
    public void ValidateUser_NeedsASignedInCaller()
    {
        Assert.NotNull(typeof(UserValidationGrpcService).GetCustomAttribute<AuthorizeAttribute>());
    }

    [Fact]
    public async Task ValidateUser_Owner_IsValidWithEveryPermissionAndAllChannels()
    {
        Mock<IWorkspaceAccessService> access = AccessReturns(WorkspaceAccess.ForOwner(new Workspace { Id = WorkspaceId, OwnerId = UserId }));

        ValidateUserResponse response = await Users(access).ValidateUser(UserRequest(), Call(SessionToken()));

        Assert.True(response.Valid);
        Assert.Equal("Owner", response.Access.Role);
        Assert.Equal(WorkspacePermissions.Effective(WorkspaceAccess.ForOwner(new Workspace { Id = WorkspaceId, OwnerId = UserId })), response.Access.Permissions);
        Assert.Empty(response.Access.Channels);
    }

    [Fact]
    public async Task ValidateUser_Member_GetsTheirLiveGrant()
    {
        WorkspaceAccess grant = new(
            new Workspace { Id = WorkspaceId, OwnerId = "owner-1" },
            "Agent manager",
            new HashSet<string> { WorkspacePermissions.ConversationsView },
            new HashSet<string> { "whatsapp" },
            new HashSet<Guid>());
        Mock<IWorkspaceAccessService> access = AccessReturns(grant);

        ValidateUserResponse response = await Users(access).ValidateUser(UserRequest(), Call(SessionToken()));

        Assert.True(response.Valid);
        Assert.Equal("Agent manager", response.Access.Role);
        Assert.Equal([WorkspacePermissions.ConversationsView], response.Access.Permissions);
        Assert.Equal(["whatsapp"], response.Access.Channels);
    }

    [Fact]
    public async Task ValidateUser_RemovedFromTheWorkspace_IsNotValid()
    {
        ValidateUserResponse response = await Users(AccessReturns(null)).ValidateUser(UserRequest(), Call(SessionToken()));

        Assert.False(response.Valid);
        Assert.Null(response.Access);
    }

    [Theory]
    [InlineData("user-2", "tenant-1", WorkspaceId)]
    [InlineData(UserId, "tenant-2", WorkspaceId)]
    [InlineData(UserId, "tenant-1", "ws-2")]
    public async Task ValidateUser_IdsOtherThanTheTokens_AreNotValid(string user, string tenant, string workspace)
    {
        Mock<IWorkspaceAccessService> access = new();

        ValidateUserResponse response = await Users(access).ValidateUser(
            new ValidateUserRequest { UserId = user, TenantId = tenant, WorkspaceId = workspace }, Call(SessionToken()));

        Assert.False(response.Valid);
        access.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ValidateUser_ServiceToken_IsUnauthenticated()
    {
        RpcException refused = await Assert.ThrowsAsync<RpcException>(() =>
            Users(new Mock<IWorkspaceAccessService>()).ValidateUser(UserRequest(), Call(ServiceToken())));
        Assert.Equal(StatusCode.Unauthenticated, refused.StatusCode);
    }

    // ---- helpers --------------------------------------------------------------

    private static OwnershipGrpcService Ownership(Mock<IConnectionService> connections)
        => new(connections.Object, NullLogger<OwnershipGrpcService>.Instance);

    private static UserValidationGrpcService Users(Mock<IWorkspaceAccessService> access)
        => new(access.Object, NullLogger<UserValidationGrpcService>.Instance);

    private static Mock<IWorkspaceAccessService> AccessReturns(WorkspaceAccess? answer)
    {
        Mock<IWorkspaceAccessService> access = new();
        access.Setup(a => a.GetAsync(UserId, WorkspaceId)).ReturnsAsync(answer);
        return access;
    }

    private static VerifyOwnershipRequest OwnershipRequest(string workspace = WorkspaceId)
        => new() { ChannelId = Connection.ToString(), TenantId = workspace, WorkspaceId = workspace };

    private static ValidateUserRequest UserRequest()
        => new() { UserId = UserId, TenantId = "tenant-1", WorkspaceId = WorkspaceId };

    // What Omni-AI mints: agent:service, the workspace as tenant and sub, svc = omni-ai.
    private static ClaimsPrincipal ServiceToken() => Principal(
        new Claim(ServiceScopes.ScopeClaim, ServiceScopes.AgentService),
        new Claim(ServiceScopes.TenantClaim, WorkspaceId),
        new Claim("sub", WorkspaceId),
        new Claim(ServiceScopes.ServiceClaim, "omni-ai"));

    private static ClaimsPrincipal SessionToken() => Principal(
        new Claim(ClaimTypes.NameIdentifier, UserId),
        new Claim("workspace_id", WorkspaceId),
        new Claim(ServiceScopes.TenantClaim, "tenant-1"));

    private static ClaimsPrincipal Principal(params Claim[] claims) => new(new ClaimsIdentity(claims, "Bearer"));

    private static ServerCallContext Call(ClaimsPrincipal user) => new TestCallContext(new DefaultHttpContext { User = user });

    // GetHttpContext() reads the "__HttpContext" user-state entry when the context is not the server's own.
    private sealed class TestCallContext(HttpContext http) : ServerCallContext
    {
        private readonly Dictionary<object, object> _state = new() { ["__HttpContext"] = http };

        protected override string MethodCore => "test";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "test";
        protected override DateTime DeadlineCore => DateTime.MaxValue;
        protected override Metadata RequestHeadersCore => [];
        protected override CancellationToken CancellationTokenCore => CancellationToken.None;
        protected override Metadata ResponseTrailersCore => [];
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => new(null, []);
        protected override IDictionary<object, object> UserStateCore => _state;

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => throw new NotSupportedException();

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }
}
