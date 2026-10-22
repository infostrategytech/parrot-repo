using Parrot.Application.Teams;
using Parrot.Domain.Entities;
using Parrot.Domain.Enums;
using Test.Parrot.Api.Tests.Services.Workspaces;

namespace Test.Parrot.Api.Tests.Services.Teams;

// What a person can do in a workspace: the owner everything, a member what
// their grant there says, or else what their all-workspaces grant says.
public class WorkspaceAccessTests
{
    private const string Owner = "owner-1";
    private const string Ada = "ada";

    private readonly FakeWorkspaceRepository _workspaces = new();
    private readonly FakeTeamRepository _team = new();
    private readonly FakeConnectionDirectory _connections = new();

    public WorkspaceAccessTests()
    {
        _workspaces.Seed("ws-1", Owner, name: "Bakery");
        _workspaces.Seed("ws-2", Owner, name: "Catering");
    }

    private WorkspaceAccessService Sut() => TeamFixture.Access(_workspaces, _team, _connections);

    [Fact]
    public async Task TheOwner_MayDoEverything_OnEveryChannel()
    {
        WorkspaceAccess? access = await Sut().GetAsync(Owner, "ws-1");

        Assert.NotNull(access);
        Assert.True(access.IsOwner);
        Assert.True(access.Can(WorkspacePermissions.TeamManage));
        Assert.True(access.CanReach(Guid.NewGuid()));
    }

    [Fact]
    public async Task SomeoneWithNoGrant_HasNoAccess()
    {
        Assert.Null(await Sut().GetAsync(Ada, "ws-1"));
    }

    [Fact]
    public async Task AGrantForOneWorkspace_DoesNotReachAnother()
    {
        _team.Grant(Owner, Ada, "ws-1", WorkspaceRoles.Analyst);

        Assert.NotNull(await Sut().GetAsync(Ada, "ws-1"));
        Assert.Null(await Sut().GetAsync(Ada, "ws-2"));
    }

    [Fact]
    public async Task AnAllWorkspacesGrant_ReachesAWorkspaceMadeAfterIt()
    {
        _team.Grant(Owner, Ada, null, WorkspaceRoles.Analyst);
        _workspaces.Seed("ws-3", Owner, name: "Later");

        WorkspaceAccess? access = await Sut().GetAsync(Ada, "ws-3");

        Assert.NotNull(access);
        Assert.True(access.Can(WorkspacePermissions.AnalyticsDashboard));
    }

    // "Specific replaces all": a workspace's own grant narrows what the all-workspaces one gives.
    [Fact]
    public async Task AWorkspacesOwnGrant_ReplacesTheAllWorkspacesOne()
    {
        _team.Grant(Owner, Ada, null, WorkspaceRoles.ConversationManager);
        _team.Grant(Owner, Ada, "ws-2", WorkspaceRoles.Analyst);

        WorkspaceAccess first = (await Sut().GetAsync(Ada, "ws-1"))!;
        WorkspaceAccess second = (await Sut().GetAsync(Ada, "ws-2"))!;

        Assert.True(first.Can(WorkspacePermissions.ConversationsReply));
        Assert.Equal(WorkspaceRoles.Analyst, second.Role);
        Assert.False(second.Can(WorkspacePermissions.ConversationsReply));
    }

    [Fact]
    public async Task AnotherOwnersGrant_DoesNotReachThisOwnersWorkspaces()
    {
        _team.Grant("someone-else", Ada, null, WorkspaceRoles.Analyst);

        Assert.Null(await Sut().GetAsync(Ada, "ws-1"));
    }

    // WhatsApp in one workspace, Instagram in the other.
    [Fact]
    public async Task AChannelLimit_ReachesOnlyThatPlatformsConnections()
    {
        Guid whatsApp = _connections.Add("ws-1", MessagingPlatform.WhatsApp);
        Guid instagram = _connections.Add("ws-1", MessagingPlatform.Instagram);
        Guid otherWhatsApp = _connections.Add("ws-2", MessagingPlatform.WhatsApp);
        Guid otherInstagram = _connections.Add("ws-2", MessagingPlatform.Instagram);
        _team.Grant(Owner, Ada, "ws-1", WorkspaceRoles.ConversationManager, [WorkspaceChannels.WhatsApp]);
        _team.Grant(Owner, Ada, "ws-2", WorkspaceRoles.ConversationManager, [WorkspaceChannels.Instagram]);

        WorkspaceAccess first = (await Sut().GetAsync(Ada, "ws-1"))!;
        WorkspaceAccess second = (await Sut().GetAsync(Ada, "ws-2"))!;

        Assert.True(first.CanReach(whatsApp));
        Assert.False(first.CanReach(instagram));
        Assert.False(second.CanReach(otherWhatsApp));
        Assert.True(second.CanReach(otherInstagram));
        Assert.False(first.AllChannels);
    }

    [Fact]
    public async Task AnAnswer_IsKeptUntilForgotten()
    {
        WorkspaceAccessService sut = Sut();
        WorkspaceGrant grant = _team.Grant(Owner, Ada, "ws-1", WorkspaceRoles.Analyst);
        Assert.NotNull(await sut.GetAsync(Ada, "ws-1"));

        _team.Grants.Remove(grant);
        Assert.NotNull(await sut.GetAsync(Ada, "ws-1"));

        await sut.ForgetAsync(Owner, Ada);
        Assert.Null(await sut.GetAsync(Ada, "ws-1"));
    }

    [Fact]
    public void TheToken_CarriesShortCodes()
    {
        Workspace workspace = new() { Id = "ws-1", OwnerId = Owner };
        WorkspaceGrant grant = new()
        {
            OwnerId = Owner,
            UserId = Ada,
            SystemRole = "conversation-manager",
            Channels = [WorkspaceChannels.WhatsApp, WorkspaceChannels.Instagram],
        };
        WorkspaceAccess member = WorkspaceAccess.ForGrant(workspace, grant, WorkspaceRoles.ConversationManager,
            [WorkspacePermissions.ConversationsView, WorkspacePermissions.ConversationsReply], []);

        Assert.Equal("cv cr", WorkspacePermissions.Abbreviate(member));
        Assert.Equal("wa ig", WorkspaceChannels.Abbreviate(member));
        Assert.Equal([WorkspacePermissions.ConversationsView, WorkspacePermissions.ConversationsReply], WorkspacePermissions.Expand("cv cr"));
        Assert.Equal("*", WorkspacePermissions.Abbreviate(WorkspaceAccess.ForOwner(workspace)));
        Assert.Equal("*", WorkspaceChannels.Abbreviate(WorkspaceAccess.ForOwner(workspace)));
    }

    // The codes go out in tokens, so two permissions must never share one.
    [Fact]
    public void EveryCode_IsUnique()
    {
        Assert.Equal(WorkspacePermissions.Grantable.Count, WorkspacePermissions.Grantable.Select(p => p.Code).Distinct().Count());
        Assert.Equal(WorkspaceChannels.All.Count, WorkspaceChannels.All.Select(c => c.Code).Distinct().Count());
    }

    [Fact]
    public async Task AMember_SeesTheWorkspacesTheirGrantsReach_WithTheirRole()
    {
        _workspaces.Seed("own", Ada, name: "Ada's own", createdAt: DateTime.UtcNow.AddDays(-5));
        _team.Grant(Owner, Ada, null, WorkspaceRoles.Analyst);
        _team.Grant(Owner, Ada, "ws-2", WorkspaceRoles.ConversationManager);

        IReadOnlyList<global::Parrot.Application.DTOs.Workspaces.WorkspaceResponse> listed = await TeamFixture.Workspaces(_workspaces, team: _team).ListAsync(Ada, "own");

        Assert.Equal(["own", "ws-1", "ws-2"], listed.Select(w => w.Id));
        Assert.Equal(["Owner", WorkspaceRoles.Analyst, WorkspaceRoles.ConversationManager], listed.Select(w => w.Role));
    }
}
