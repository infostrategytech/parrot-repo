using Microsoft.AspNetCore.Identity;
using Moq;
using Parrot.Application.DTOs.Workspaces;
using Parrot.Application.Logging;
using Parrot.Application.Models;
using Parrot.Application.Profile;
using Parrot.Application.Teams;
using Parrot.Application.Workspaces;
using Parrot.Domain.Exceptions;
using Test.Parrot.Api.Tests.Services.Workspaces;

namespace Test.Parrot.Api.Tests.Services.Teams;

// "When I sign in": resume where you left off (the default), or always open one workspace.
public class DefaultWorkspaceTests
{
    private const string Ada = "ada";

    private readonly FakeWorkspaceRepository _workspaces = new();
    private readonly FakeTeamRepository _team = new();
    private readonly WorkspaceService _service;

    public DefaultWorkspaceTests()
    {
        _workspaces.Seed("a", Ada, name: "A", createdAt: DateTime.UtcNow.AddDays(-3));
        _workspaces.Seed("b", Ada, name: "B", createdAt: DateTime.UtcNow.AddDays(-2));
        _workspaces.Seed("oma-1", "oma", name: "Oma's Bakery", createdAt: DateTime.UtcNow.AddDays(-1));
        _service = TeamFixture.Workspaces(_workspaces, team: _team);
    }

    // Signed out (or the session expired) in B: back in B, with no default set.
    [Fact]
    public async Task WithNoDefault_SignInResumesTheLastWorkspace()
    {
        ApplicationUser ada = new() { Id = Ada, LastWorkspaceId = "b" };

        Assert.Equal("b", (await _service.ResolveForSignInAsync(ada)).Workspace.Id);
    }

    [Fact]
    public async Task ADefault_IsOpenedWhereverTheyLeftOff()
    {
        ApplicationUser ada = new() { Id = Ada, LastWorkspaceId = "b", DefaultWorkspaceId = "a" };

        Assert.Equal("a", (await _service.ResolveForSignInAsync(ada)).Workspace.Id);
    }

    [Fact]
    public async Task ADefaultCanBeAnotherBusinesssWorkspace()
    {
        _team.Grant("oma", Ada, "oma-1", WorkspaceRoles.Analyst);
        ApplicationUser ada = new() { Id = Ada, LastWorkspaceId = "b", DefaultWorkspaceId = "oma-1" };

        Assert.Equal("oma-1", (await _service.ResolveForSignInAsync(ada)).Workspace.Id);
    }

    // Removed from the business whose workspace was their default: they resume, not stop at the door.
    [Fact]
    public async Task ADefaultTheyCanNoLongerReach_FallsBackToResuming()
    {
        ApplicationUser ada = new() { Id = Ada, LastWorkspaceId = "b", DefaultWorkspaceId = "oma-1" };

        Assert.Equal("b", (await _service.ResolveForSignInAsync(ada)).Workspace.Id);
    }

    [Fact]
    public async Task TheList_MarksTheDefault()
    {
        IReadOnlyList<WorkspaceResponse> listed = await _service.ListAsync(Ada, "b", "a");

        Assert.Equal(["a"], listed.Where(w => w.IsDefault).Select(w => w.Id));
    }

    private ProfileService Profile(ApplicationUser user, Mock<UserManager<ApplicationUser>> users)
    {
        users.Setup(u => u.FindByIdAsync(user.Id)).ReturnsAsync(user);
        users.Setup(u => u.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);
        Mock<IAppLogger<ProfileService>> logger = new();
        logger.Setup(l => l.BeginEntityScope(It.IsAny<string>(), It.IsAny<string>())).Returns(Mock.Of<IDisposable>());
        return new ProfileService(users.Object, _service, logger.Object);
    }

    private static Mock<UserManager<ApplicationUser>> Users() =>
        new(Mock.Of<IUserStore<ApplicationUser>>(), null!, null!, null!, null!, null!, null!, null!, null!);

    [Fact]
    public async Task SettingADefault_SavesIt_AndClearingItResumesAgain()
    {
        ApplicationUser ada = new() { Id = Ada };
        ProfileService sut = Profile(ada, Users());

        await sut.SetDefaultWorkspaceAsync(Ada, "a");
        Assert.Equal("a", ada.DefaultWorkspaceId);

        await sut.SetDefaultWorkspaceAsync(Ada, null);
        Assert.Null(ada.DefaultWorkspaceId);
    }

    [Fact]
    public async Task ADefaultTheyCannotReach_IsRefused()
    {
        ApplicationUser ada = new() { Id = Ada };
        Mock<UserManager<ApplicationUser>> users = Users();

        await Assert.ThrowsAsync<NotFoundException>(() => Profile(ada, users).SetDefaultWorkspaceAsync(Ada, "oma-1"));
        Assert.Null(ada.DefaultWorkspaceId);
        users.Verify(u => u.UpdateAsync(It.IsAny<ApplicationUser>()), Times.Never);
    }
}
