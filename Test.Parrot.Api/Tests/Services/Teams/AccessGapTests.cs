using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Parrot.Api.Controllers;
using Parrot.Api.Realtime;
using Parrot.Api.Security;
using Parrot.Application.Agents;
using Parrot.Application.DTOs.Agents;
using Parrot.Application.DTOs.Workspaces;
using Parrot.Application.Teams;
using Parrot.Application.Workspaces;
using Parrot.Domain.Entities;
using Parrot.Domain.Exceptions;
using StackExchange.Redis;
using Test.Parrot.Api.Tests.Services.Workspaces;

namespace Test.Parrot.Api.Tests.Services.Teams;

// The gaps closed after the first teams pass: agents by channel and permission,
// access refreshed from the grant, and cached answers forgotten on every instance.
public class AccessGapTests
{
    private const string Owner = "owner-1";

    private readonly FakeWorkspaceRepository _workspaces = new();
    private readonly FakeTeamRepository _team = new();

    public AccessGapTests() => _workspaces.Seed("ws-1", Owner, name: "Bakery");

    private async Task<HttpContext> SessionAsync(string userId)
    {
        DefaultHttpContext http = new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId), new Claim(WorkspaceClaims.WorkspaceId, "ws-1")], "Bearer")),
        };
        http.Items[typeof(WorkspaceAccess)] = await TeamFixture.Access(_workspaces, _team).GetAsync(userId, "ws-1");
        return http;
    }

    private static AgentResponse Agent(params string[] channels) => new() { Id = Guid.NewGuid(), Name = "Agent", Channels = [.. channels] };

    private static AgentController Agents(HttpContext http, params AgentResponse[] agents)
    {
        Mock<IAgentService> service = new();
        service.Setup(s => s.GetAllAsync("ws-1")).ReturnsAsync(agents);
        foreach (AgentResponse agent in agents)
        {
            service.Setup(s => s.GetByIdAsync("ws-1", agent.Id)).ReturnsAsync(agent);
        }

        return new AgentController(service.Object) { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    [Fact]
    public async Task AChannelLimitedMember_SeesOnlyAgentsOnTheirChannels()
    {
        _team.Grant(Owner, "ada", "ws-1", WorkspaceRoles.ConversationManager, [WorkspaceChannels.WhatsApp]);
        AgentResponse whatsApp = Agent("whatsapp", "instagram");
        AgentResponse instagram = Agent("instagram");
        AgentController controller = Agents(await SessionAsync("ada"), whatsApp, instagram);

        OkObjectResult listed = Assert.IsType<OkObjectResult>(await controller.GetAgents());

        Assert.Equal([whatsApp.Id], ((IEnumerable<AgentResponse>)listed.Value!).Select(a => a.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => controller.GetAgent(instagram.Id));
    }

    [Fact]
    public async Task AChannelLimitedMember_CannotPutAnAgentOnAnotherChannel()
    {
        _team.Grant(Owner, "ada", "ws-1", WorkspaceRoles.AutomationManager, [WorkspaceChannels.WhatsApp]);
        AgentController controller = Agents(await SessionAsync("ada"));

        await Assert.ThrowsAsync<ForbiddenException>(() => controller.CreateAgent(new AgentInfo { Name = "x", Description = "x", MascotUrl = "https://cdn.example.com/m.png", Channels = ["instagram"], SupportsHumanTakeover = false }));
    }

    // Reading agents needs a role that works with them; a channel or catalog manager does not.
    [Fact]
    public async Task ReadingAgents_NeedsARoleThatWorksWithThem()
    {
        _team.Grant(Owner, "cat", "ws-1", WorkspaceRoles.CatalogManager);
        _team.Grant(Owner, "ana", "ws-1", WorkspaceRoles.Analyst);
        RequireAnyPermissionAttribute readers = (RequireAnyPermissionAttribute)typeof(AgentController)
            .GetMethod(nameof(AgentController.GetAgents))!.GetCustomAttributes(typeof(RequireAnyPermissionAttribute), false).Single();

        AuthorizationFilterContext catalogManager = Filter(await SessionAsync("cat"));
        Assert.Throws<ForbiddenException>(() => readers.OnAuthorization(catalogManager));
        readers.OnAuthorization(Filter(await SessionAsync("ana")));
    }

    private static AuthorizationFilterContext Filter(HttpContext http) =>
        new(new ActionContext(http, new RouteData(), new ActionDescriptor()), []);

    [Fact]
    public async Task CurrentAccess_IsReadFromTheGrant_NotTheToken()
    {
        OwnerRole weekend = _team.Role(Owner, "Weekend Support", WorkspacePermissions.ConversationsView, WorkspacePermissions.CatalogView);
        _team.Grant("ada", "ws-1", weekend, [WorkspaceChannels.Instagram]);
        WorkspacesController controller = new(Mock.Of<IWorkspaceService>(), Mock.Of<global::Parrot.Application.Auth.IAuthService>(), Mock.Of<global::Parrot.Application.Profile.IProfileService>())
        {
            ControllerContext = new ControllerContext { HttpContext = await SessionAsync("ada") },
        };

        SessionAccessResponse access = Assert.IsType<SessionAccessResponse>(Assert.IsType<OkObjectResult>(controller.CurrentAccess().Result).Value);

        Assert.Equal(("ws-1", "Weekend Support", true), (access.WorkspaceId, access.Role, access.IsCustomRole));
        Assert.Equal([WorkspacePermissions.ConversationsView, WorkspacePermissions.CatalogView], access.Permissions);
        Assert.Equal([WorkspaceChannels.Instagram], access.Channels);
        Assert.Equal("catalog:read", access.CatalogScopes);
    }

    private sealed class RecordingBroadcast : IAccessCacheBroadcast
    {
        public List<string> Published { get; } = [];

        public void Publish(IReadOnlyList<string> cacheKeys) => Published.AddRange(cacheKeys);
    }

    [Fact]
    public async Task ForgettingAnAnswer_IsBroadcastToTheOtherInstances()
    {
        _workspaces.Seed("ws-2", Owner);
        RecordingBroadcast broadcast = new();
        WorkspaceAccessService access = new(_workspaces, _team, new FakeConnectionDirectory(), new MemoryCache(new MemoryCacheOptions()), broadcast);

        await access.ForgetAsync(Owner, "ada");

        Assert.Equal([WorkspaceAccessService.Key("ws-1", "ada"), WorkspaceAccessService.Key("ws-2", "ada")], broadcast.Published);
    }

    // What another instance does with the broadcast: forget those answers, and nothing else.
    [Fact]
    public void ABroadcast_ForgetsThoseAnswers_AndIgnoresAnythingElse()
    {
        MemoryCache cache = new(new MemoryCacheOptions());
        string answer = WorkspaceAccessService.Key("ws-1", "ada");
        cache.Set(answer, "cached");
        cache.Set("some-other-entry", "kept");
        RedisAccessCacheBroadcast receiver = new(Mock.Of<IConnectionMultiplexer>(), cache, NullLogger<RedisAccessCacheBroadcast>.Instance);

        receiver.Receive($"{answer}\nsome-other-entry");

        Assert.False(cache.TryGetValue(answer, out _));
        Assert.True(cache.TryGetValue("some-other-entry", out _));
    }
}
