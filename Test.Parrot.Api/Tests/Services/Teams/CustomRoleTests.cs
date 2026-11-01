using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Moq;
using Parrot.Application.DTOs.Teams;
using Parrot.Application.Email;
using Parrot.Application.Logging;
using Parrot.Application.Models;
using Parrot.Application.Teams;
using Parrot.Domain.Entities;
using Parrot.Domain.Exceptions;
using Test.Parrot.Api.Tests.Services.Workspaces;

namespace Test.Parrot.Api.Tests.Services.Teams;

// Built-in roles are read-only and the same everywhere; an owner makes roles of their own, often by duplicating one.
public class CustomRoleTests
{
    private const string Owner = "owner-1";

    private readonly FakeWorkspaceRepository _workspaces = new();
    private readonly FakeTeamRepository _team = new();
    private readonly WorkspaceAccessService _access;
    private readonly TeamService _sut;

    public CustomRoleTests()
    {
        _workspaces.Seed("ws-1", Owner, name: "Bakery");
        _workspaces.Seed("theirs", "owner-2", name: "Not yours");
        _access = TeamFixture.Access(_workspaces, _team);

        Mock<UserManager<ApplicationUser>> users = new(Mock.Of<IUserStore<ApplicationUser>>(), null!, null!, null!, null!, null!, null!, null!, null!);
        users.Setup(u => u.NormalizeEmail(It.IsAny<string>())).Returns<string>(e => e.ToUpperInvariant());
        Mock<IWebHostEnvironment> env = new();
        env.SetupGet(e => e.EnvironmentName).Returns("Development");
        _sut = new TeamService(_team, _workspaces, _access, users.Object, Mock.Of<IEmailService>(),
            Options.Create(new EmailSettings { SmtpHost = "s", SmtpUser = "u", SmtpPassword = "p", FromAddress = "f@x.com", FromName = "O", BaseUrl = "https://app" }),
            env.Object, TeamFixture.InviteTokens(), TimeProvider.System, Mock.Of<IAppLogger<TeamService>>());
    }

    private async Task<WorkspaceAccess> OwnerAccess(string owner = Owner, string ws = "ws-1") => (await _access.GetAsync(owner, ws))!;

    private static SaveRoleRequest Save(string name, params string[] permissions) => new() { Name = name, Permissions = permissions };

    [Fact]
    public async Task BuiltInRoles_CannotBeChangedOrDeleted()
    {
        WorkspaceAccess owner = await OwnerAccess();

        await Assert.ThrowsAsync<ForbiddenException>(() => _sut.UpdateRoleAsync(owner, "analyst", Save("Analyst", WorkspacePermissions.ConversationsReply)));
        await Assert.ThrowsAsync<ForbiddenException>(() => _sut.DeleteRoleAsync(owner, "analyst"));
    }

    [Fact]
    public async Task Duplicating_MakesAnEditableCopy_NamedCopy_ThenCopy2()
    {
        WorkspaceAccess owner = await OwnerAccess();

        RoleResponse first = await _sut.DuplicateRoleAsync(owner, "analyst");
        RoleResponse second = await _sut.DuplicateRoleAsync(owner, "analyst");

        Assert.Equal(("Analyst (copy)", true), (first.Name, first.IsCustom));
        Assert.Equal("Analyst (copy 2)", second.Name);
        Assert.Equal(WorkspaceRoles.FindById("analyst")!.Permissions, first.Permissions);
    }

    // A copy may take the built-in's own name; custom roles are always marked as such.
    [Fact]
    public async Task ACopy_CanBeRenamedToTheBuiltInsName_ButTwoOwnRolesCannotShareOne()
    {
        WorkspaceAccess owner = await OwnerAccess();
        RoleResponse copy = await _sut.DuplicateRoleAsync(owner, "analyst");

        RoleResponse renamed = await _sut.UpdateRoleAsync(owner, copy.Id, Save("Analyst", WorkspacePermissions.AnalyticsDashboard));
        Assert.Equal(("Analyst", true), (renamed.Name, renamed.IsCustom));

        await Assert.ThrowsAsync<ConflictException>(() => _sut.CreateRoleAsync(owner, Save("analyst", WorkspacePermissions.AnalyticsQa)));
    }

    [Fact]
    public async Task OwnerIsNotARoleName()
    {
        await Assert.ThrowsAsync<ValidationException>(async () =>
            await _sut.CreateRoleAsync(await OwnerAccess(), Save("owner", WorkspacePermissions.AnalyticsQa)));
    }

    [Fact]
    public async Task EditingARole_ReachesEveryoneHoldingIt_AtOnce()
    {
        OwnerRole weekend = _team.Role(Owner, "Weekend Support", WorkspacePermissions.ConversationsView);
        _team.Grant("ada", "ws-1", weekend);
        WorkspaceAccess before = (await _access.GetAsync("ada", "ws-1"))!;
        Assert.Equal(("Weekend Support", true), (before.Role, before.IsCustomRole));
        Assert.False(before.Can(WorkspacePermissions.ConversationsReply));

        await _sut.UpdateRoleAsync(await OwnerAccess(), weekend.Id.ToString(),
            Save("Weekend Support", WorkspacePermissions.ConversationsView, WorkspacePermissions.ConversationsReply));

        Assert.True((await _access.GetAsync("ada", "ws-1"))!.Can(WorkspacePermissions.ConversationsReply));
    }

    [Fact]
    public async Task ARoleInUse_CannotBeDeleted()
    {
        OwnerRole weekend = _team.Role(Owner, "Weekend Support", WorkspacePermissions.ConversationsView);
        WorkspaceGrant held = _team.Grant("ada", "ws-1", weekend);

        await Assert.ThrowsAsync<ConflictException>(async () => await _sut.DeleteRoleAsync(await OwnerAccess(), weekend.Id.ToString()));

        _team.Grants.Remove(held);
        await _sut.DeleteRoleAsync(await OwnerAccess(), weekend.Id.ToString());
        Assert.Empty(_team.Roles);
    }

    [Fact]
    public async Task ACustomRole_CanBeGranted_AndIsCountedAsHeld()
    {
        WorkspaceAccess owner = await OwnerAccess();
        RoleResponse role = await _sut.CreateRoleAsync(owner, Save("Night Shift", WorkspacePermissions.ConversationsView));

        await _sut.InviteAsync(owner, Owner, new CreateInviteRequest
        {
            FullName = "Bob", Email = "bob@example.com", Grants = [new GrantDto { WorkspaceId = "ws-1", RoleId = role.Id }],
        });

        GrantSpec spec = Assert.Single(Assert.Single(_team.Invites).Grants);
        Assert.Equal(Guid.Parse(role.Id), spec.CustomRoleId);
        Assert.Null(spec.SystemRole);
        // A pending invite naming it also keeps it from being deleted.
        await Assert.ThrowsAsync<ConflictException>(() => _sut.DeleteRoleAsync(owner, role.Id));
    }

    // Scoped to the owner: another owner neither sees nor can grant it.
    [Fact]
    public async Task AnotherOwnersRole_IsNotListed_AndCannotBeGranted()
    {
        OwnerRole theirs = _team.Role("owner-2", "Their Role", WorkspacePermissions.ConversationsView);
        WorkspaceAccess owner = await OwnerAccess();

        TeamRolesResponse roles = await _sut.GetRolesAsync(owner);
        Assert.DoesNotContain(roles.Roles, r => r.Id == theirs.Id.ToString());
        Assert.Equal(WorkspaceRoles.All.Count, roles.Roles.Count);

        await Assert.ThrowsAsync<ValidationException>(() => _sut.InviteAsync(owner, Owner, new CreateInviteRequest
        {
            FullName = "Bob", Email = "bob@example.com", Grants = [new GrantDto { WorkspaceId = "ws-1", RoleId = theirs.Id.ToString() }],
        }));
        await Assert.ThrowsAsync<NotFoundException>(() => _sut.UpdateRoleAsync(owner, theirs.Id.ToString(), Save("Mine now", WorkspacePermissions.ConversationsView)));
    }
}
