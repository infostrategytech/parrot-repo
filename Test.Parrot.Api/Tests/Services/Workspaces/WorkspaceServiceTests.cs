using Test.Parrot.Api.Tests.Services.Teams;
using Moq;
using Parrot.Application.Auth;
using Parrot.Application.DTOs.Workspaces;
using Parrot.Application.Logging;
using Parrot.Application.Models;
using Parrot.Application.Workspaces;
using Parrot.Domain.Entities;
using Parrot.Domain.Exceptions;

namespace Test.Parrot.Api.Tests.Services.Workspaces;

public class WorkspaceServiceTests
{
    private const string Tenant = "tenant-1";

    private readonly FakeWorkspaceRepository _repository = new();
    private readonly WorkspaceService _sut;

    public WorkspaceServiceTests()
    {
        Mock<IAppLogger<WorkspaceService>> logger = new Mock<IAppLogger<WorkspaceService>>();
        logger
            .Setup(l => l.BeginEntityScope(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Mock.Of<IDisposable>());

        _sut = TeamFixture.Workspaces(_repository, logger.Object);
    }

    // ---------------------------------------------------------------------------
    // ListAsync
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ListAsync_ReturnsOnlyTheTenantsOwn_OldestFirst_WithTheCurrentMarked()
    {
        _repository.Seed("second", Tenant, name: "Second", createdAt: DateTime.UtcNow.AddDays(-1));
        _repository.Seed("first", Tenant, name: "First", createdAt: DateTime.UtcNow.AddDays(-2));
        _repository.Seed("theirs", "another-tenant", name: "Theirs");

        IReadOnlyList<WorkspaceResponse> listed = await _sut.ListAsync(Tenant, currentWorkspaceId: "second");

        Assert.Equal(["first", "second"], listed.Select(w => w.Id));
        Assert.False(listed[0].IsCurrent);
        Assert.True(listed[1].IsCurrent);
    }

    // ---------------------------------------------------------------------------
    // CreateAsync
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task CreateAsync_MakesAWorkspaceOwnedByTheTenant_WithTheNameTrimmed()
    {
        WorkspaceResponse created = await _sut.CreateAsync(Tenant, new CreateWorkspaceRequest
        {
            Name = "  Oma's Kitchen ",
            LogoUrl = "https://cdn.example.com/logo.png",
        });

        Workspace saved = _repository.Rows[created.Id];
        Assert.Equal(Tenant, saved.OwnerId);
        Assert.Equal("Oma's Kitchen", saved.Name);
        Assert.Equal("https://cdn.example.com/logo.png", saved.LogoUrl);
        Assert.NotEqual(Tenant, created.Id);
        Assert.False(created.IsCurrent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateAsync_WithABlankName_IsRefused(string name)
    {
        ValidationException ex = await Assert.ThrowsAsync<ValidationException>(() =>
            _sut.CreateAsync(Tenant, new CreateWorkspaceRequest { Name = name }));

        Assert.Equal(AuthMessages.BusinessNameTooLong, ex.Message);
        Assert.Empty(_repository.Rows);
    }

    [Fact]
    public async Task CreateAsync_WithALogoThatIsNotAWebUrl_IsRefused()
    {
        ValidationException ex = await Assert.ThrowsAsync<ValidationException>(() =>
            _sut.CreateAsync(Tenant, new CreateWorkspaceRequest { Name = "Shop", LogoUrl = "ftp://cdn.example.com/logo.png" }));

        Assert.Equal(AuthMessages.InvalidLogoUrl, ex.Message);
        Assert.Empty(_repository.Rows);
    }

    // ---------------------------------------------------------------------------
    // UpdateAsync
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task UpdateAsync_ChangesOnlyWhatIsSent_AndAnEmptyLogoRemovesIt()
    {
        _repository.Seed("ws", Tenant, name: "Before", logoUrl: "https://cdn.example.com/logo.png");

        WorkspaceResponse renamed = await _sut.UpdateAsync(Tenant, "ws", new UpdateWorkspaceRequest { Name = "After" });
        Assert.Equal("After", renamed.Name);
        Assert.Equal("https://cdn.example.com/logo.png", renamed.LogoUrl);

        WorkspaceResponse cleared = await _sut.UpdateAsync(Tenant, "ws", new UpdateWorkspaceRequest { LogoUrl = string.Empty });
        Assert.Equal("After", cleared.Name);
        Assert.Null(cleared.LogoUrl);
    }

    [Fact]
    public async Task UpdateAsync_OnAWorkspaceSomeoneElseOwns_IsNotFound_AndChangesNothing()
    {
        Workspace theirs = _repository.Seed("theirs", "another-tenant", name: "Theirs");

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _sut.UpdateAsync(Tenant, "theirs", new UpdateWorkspaceRequest { Name = "Mine Now" }));

        Assert.Equal("Theirs", theirs.Name);
        Assert.Equal(0, _repository.Updates);
    }

    // ---------------------------------------------------------------------------
    // ResolveForSignInAsync
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ResolveForSignInAsync_WithNoWorkspace_MakesTheFirstFromTheAccount()
    {
        ApplicationUser user = new() { Id = Tenant, BusinessName = "Oma's Kitchen", LogoUrl = "https://cdn.example.com/logo.png" };

        Workspace workspace = (await _sut.ResolveForSignInAsync(user)).Workspace;

        // The user's own id, so whatever the account wrote before still belongs to it.
        Assert.Equal(Tenant, workspace.Id);
        Assert.Equal(Tenant, workspace.OwnerId);
        Assert.Equal("Oma's Kitchen", workspace.Name);
        Assert.Equal("https://cdn.example.com/logo.png", workspace.LogoUrl);
        Assert.Same(workspace, _repository.Rows[Tenant]);
    }

    [Fact]
    public async Task ResolveForSignInAsync_PrefersTheWorkspaceSwitchedToLast()
    {
        _repository.Seed("first", Tenant, createdAt: DateTime.UtcNow.AddDays(-2));
        _repository.Seed("second", Tenant, createdAt: DateTime.UtcNow.AddDays(-1));
        ApplicationUser user = new() { Id = Tenant, LastWorkspaceId = "second" };

        Workspace workspace = (await _sut.ResolveForSignInAsync(user)).Workspace;

        Assert.Equal("second", workspace.Id);
    }

    // A remembered id that is no longer the tenant's own is ignored, never
    // trusted: the session lands in the oldest workspace they do own.
    [Fact]
    public async Task ResolveForSignInAsync_IgnoresALastWorkspaceTheyDoNotOwn()
    {
        _repository.Seed("first", Tenant, createdAt: DateTime.UtcNow.AddDays(-2));
        _repository.Seed("theirs", "another-tenant");
        ApplicationUser user = new() { Id = Tenant, LastWorkspaceId = "theirs" };

        Workspace workspace = (await _sut.ResolveForSignInAsync(user)).Workspace;

        Assert.Equal("first", workspace.Id);
    }
}
