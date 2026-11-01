using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Parrot.Api.Security;
using Parrot.Application.Teams;
using Parrot.Application.Workspaces;
using Parrot.Domain.Exceptions;
using Test.Parrot.Api.Tests.Services.Workspaces;

namespace Test.Parrot.Api.Tests.Services.Teams;

// Every signed-in request is checked against the person's access, and each action's permission.
public class WorkspaceAccessFilterTests
{
    private const string Owner = "owner-1";

    private readonly FakeWorkspaceRepository _workspaces = new();
    private readonly FakeTeamRepository _team = new();

    public WorkspaceAccessFilterTests() => _workspaces.Seed("ws-1", Owner);

    private static AuthorizationFilterContext Request(string userId, string workspaceId, params object[] metadata)
    {
        DefaultHttpContext http = new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId), new Claim(WorkspaceClaims.WorkspaceId, workspaceId)], "Bearer")),
        };
        ActionContext action = new(http, new RouteData(), new ActionDescriptor { EndpointMetadata = [.. metadata] });
        return new AuthorizationFilterContext(action, []);
    }

    private WorkspaceAccessFilter Filter() => new(TeamFixture.Access(_workspaces, _team));

    [Fact]
    public async Task SomeoneWithNoAccess_IsRefused()
    {
        await Assert.ThrowsAsync<ForbiddenException>(() => Filter().OnAuthorizationAsync(Request("ada", "ws-1")));
    }

    [Fact]
    public async Task ARouteForAnyWorkspace_IsLetThrough_WithoutAccess()
    {
        await Filter().OnAuthorizationAsync(Request("ada", "ws-1", new AllowWithoutWorkspaceAttribute()));
    }

    [Fact]
    public async Task AMember_IsLetThroughForWhatTheyMayDo_AndRefusedTheRest()
    {
        _team.Grant(Owner, "ada", "ws-1", WorkspaceRoles.Analyst);
        AuthorizationFilterContext request = Request("ada", "ws-1");
        await Filter().OnAuthorizationAsync(request);

        new RequirePermissionAttribute(WorkspacePermissions.AnalyticsDashboard).OnAuthorization(request);
        Assert.Throws<ForbiddenException>(() => new RequirePermissionAttribute(WorkspacePermissions.ConversationsReply).OnAuthorization(request));
    }

    [Fact]
    public async Task AChannelsSetup_IsRefusedToAMemberWhoseGrantDoesNotReachIt()
    {
        _team.Grant(Owner, "ada", "ws-1", WorkspaceRoles.ChannelManager, [WorkspaceChannels.WhatsApp]);
        RequirePermissionAttribute connect = new(WorkspacePermissions.ChannelsConnect);
        AuthorizationFilterContext request = Request("ada", "ws-1", connect);
        await Filter().OnAuthorizationAsync(request);

        new RequireChannelAttribute(WorkspaceChannels.WhatsApp).OnAuthorization(request);
        Assert.Throws<ForbiddenException>(() => new RequireChannelAttribute(WorkspaceChannels.Instagram).OnAuthorization(request));
    }
}
