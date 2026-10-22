using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Moq;
using Parrot.Application.Auth;
using Parrot.Application.DTOs.Auth;
using Parrot.Application.DTOs.Teams;
using Parrot.Application.DTOs.Workspaces;
using Parrot.Application.Email;
using Parrot.Application.Logging;
using Parrot.Application.Models;
using Parrot.Application.Teams;
using Parrot.Domain.Entities;
using Parrot.Domain.Exceptions;
using Test.Parrot.Api.Tests.Services.Workspaces;

namespace Test.Parrot.Api.Tests.Services.Teams;

// One login across every business: a person can own workspaces and work in
// other owners' at the same time, and an invite makes the login if there is none.
public class OneLoginTests
{
    private readonly FakeWorkspaceRepository _workspaces = new();
    private readonly FakeTeamRepository _team = new();
    private readonly Mock<UserManager<ApplicationUser>> _users;
    private readonly Dictionary<string, ApplicationUser> _accounts = [];
    private readonly Mock<IAuthService> _auth = new();
    private string? _sentBody;

    public OneLoginTests()
    {
        _users = new Mock<UserManager<ApplicationUser>>(Mock.Of<IUserStore<ApplicationUser>>(), null!, null!, null!, null!, null!, null!, null!, null!);
        _users.Setup(u => u.NormalizeEmail(It.IsAny<string>())).Returns<string>(e => e.ToUpperInvariant());
        _users.Setup(u => u.FindByIdAsync(It.IsAny<string>())).ReturnsAsync((string id) => _accounts.GetValueOrDefault(id));
        _users.Setup(u => u.FindByEmailAsync(It.IsAny<string>()))
            .ReturnsAsync((string email) => _accounts.Values.FirstOrDefault(a => a.NormalizedEmail == email.ToUpperInvariant()));
        _users.Setup(u => u.CreateAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()))
            .Callback<ApplicationUser, string>((user, _) =>
            {
                user.Id = "new-user";
                user.NormalizedEmail = user.Email!.ToUpperInvariant();
                _accounts[user.Id] = user;
            })
            .ReturnsAsync(IdentityResult.Success);
        _users.Setup(u => u.GeneratePasswordResetTokenAsync(It.IsAny<ApplicationUser>())).ReturnsAsync("reset");
        _users.Setup(u => u.ResetPasswordAsync(It.IsAny<ApplicationUser>(), "reset", It.IsAny<string>())).ReturnsAsync(IdentityResult.Success);
        _users.Setup(u => u.UpdateAsync(It.IsAny<ApplicationUser>())).ReturnsAsync(IdentityResult.Success);
        _auth.Setup(a => a.SwitchWorkspaceAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((string user, string ws) => new AuthResponse("token", DateTime.UtcNow.AddHours(1), user, "x", null, null, null, ws, "Analyst", []));
    }

    private ApplicationUser Account(string id, string email, bool proved = true) =>
        _accounts[id] = new ApplicationUser { Id = id, Email = email, NormalizedEmail = email.ToUpperInvariant(), EmailConfirmed = proved };

    private TeamService Team(WorkspaceAccessService access)
    {
        Mock<IWebHostEnvironment> env = new();
        env.SetupGet(e => e.EnvironmentName).Returns("Production");
        Mock<IEmailService> email = new();
        email.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string, string, string>((_, _, html, _) => _sentBody = html).Returns(Task.CompletedTask);
        return new TeamService(_team, _workspaces, access, _users.Object, email.Object,
            Options.Create(new EmailSettings { SmtpHost = "s", SmtpUser = "u", SmtpPassword = "p", FromAddress = "f@x.com", FromName = "O", BaseUrl = "https://app" }),
            env.Object, TeamFixture.InviteTokens(), TimeProvider.System, Mock.Of<IAppLogger<TeamService>>());
    }

    private string Token()
    {
        int start = _sentBody!.IndexOf("/invite/", StringComparison.Ordinal) + "/invite/".Length;
        return _sentBody[start.._sentBody.IndexOf('"', start)];
    }

    [Fact]
    public async Task APersonInSeveralBusinesses_SeesTheirOwnFirst_ThenEachBusiness_TaggedWithTheirRole()
    {
        _workspaces.Seed("ada-own", "ada", name: "Ada's Studio", createdAt: DateTime.UtcNow.AddDays(-9));
        _workspaces.Seed("oma-1", "oma", name: "Oma's Bakery", createdAt: DateTime.UtcNow.AddDays(-8));
        _workspaces.Seed("oma-2", "oma", name: "Oma's Catering", createdAt: DateTime.UtcNow.AddDays(-7));
        _workspaces.Seed("tunde-1", "tunde", name: "Tunde Motors", createdAt: DateTime.UtcNow.AddDays(-6));
        _workspaces.Seed("tunde-2", "tunde", name: "Tunde Parts", createdAt: DateTime.UtcNow.AddDays(-5));
        _team.Grant("oma", "ada", "oma-2", WorkspaceRoles.ConversationManager, [WorkspaceChannels.WhatsApp]);
        _team.Grant("tunde", "ada", null, WorkspaceRoles.Analyst);

        IReadOnlyList<WorkspaceResponse> listed = await TeamFixture.Workspaces(_workspaces, team: _team).ListAsync("ada", "oma-2");

        Assert.Equal(["ada-own", "oma-2", "tunde-1", "tunde-2"], listed.Select(w => w.Id));
        Assert.Equal(["Owner", WorkspaceRoles.ConversationManager, WorkspaceRoles.Analyst, WorkspaceRoles.Analyst], listed.Select(w => w.Role));
        Assert.Equal([null, "Oma's Bakery", "Tunde Motors", "Tunde Motors"], listed.Select(w => w.TeamName));
        Assert.Equal(["ada", "oma", "tunde", "tunde"], listed.Select(w => w.OwnerId));
        Assert.True(listed.Single(w => w.Id == "oma-2").IsCurrent);
    }

    [Fact]
    public async Task AnInvitedAddressWithNoAccount_GetsAVerifiedLogin_JoinsAndIsSignedIn()
    {
        _workspaces.Seed("oma-1", "oma", name: "Oma's Bakery");
        Account("oma", "oma@example.com");
        WorkspaceAccessService access = TeamFixture.Access(_workspaces, _team);
        TeamService team = Team(access);
        await team.InviteAsync((await access.GetAsync("oma", "oma-1"))!, "oma", new CreateInviteRequest
        {
            FullName = "Ada Obi", Email = "ada@example.com", Grants = [new GrantDto { WorkspaceId = "oma-1", RoleId = "analyst" }],
        });
        Assert.False((await team.PreviewInviteAsync(Token())).HasAccount);

        InviteSignUpService sut = new(team, _auth.Object, _users.Object, Mock.Of<IAppLogger<InviteSignUpService>>());
        AuthResponse session = await sut.SignUpAndAcceptAsync(Token(), new InviteSignUpRequest { FirstName = "Ada", LastName = "Obi", Password = "Str0ng!pass" });

        ApplicationUser made = _accounts["new-user"];
        Assert.True(made.EmailConfirmed);
        Assert.Equal(("ada@example.com", "Ada", "Obi"), (made.Email, made.FirstName, made.LastName));
        Assert.Equal("oma-1", session.WorkspaceId);
        Assert.Equal("new-user", Assert.Single(_team.Grants).UserId);
    }

    // The link alone must never set the password of an account someone already has.
    [Fact]
    public async Task AnAddressThatAlreadyHasALogin_MustSignIn()
    {
        _workspaces.Seed("oma-1", "oma", name: "Oma's Bakery");
        Account("oma", "oma@example.com");
        Account("ada", "ada@example.com");
        WorkspaceAccessService access = TeamFixture.Access(_workspaces, _team);
        TeamService team = Team(access);
        await team.InviteAsync((await access.GetAsync("oma", "oma-1"))!, "oma", new CreateInviteRequest
        {
            FullName = "Ada", Email = "ada@example.com", Grants = [new GrantDto { WorkspaceId = "oma-1", RoleId = "analyst" }],
        });
        Assert.True((await team.PreviewInviteAsync(Token())).HasAccount);

        InviteSignUpService sut = new(team, _auth.Object, _users.Object, Mock.Of<IAppLogger<InviteSignUpService>>());

        await Assert.ThrowsAsync<ConflictException>(() =>
            sut.SignUpAndAcceptAsync(Token(), new InviteSignUpRequest { FirstName = "Ada", Password = "Str0ng!pass" }));
        _users.Verify(u => u.CreateAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()), Times.Never);
        Assert.Empty(_team.Grants);
    }

    private async Task<TeamService> InviteAdaAsync()
    {
        _workspaces.Seed("oma-1", "oma", name: "Oma's Bakery");
        Account("oma", "oma@example.com");
        WorkspaceAccessService access = TeamFixture.Access(_workspaces, _team);
        TeamService team = Team(access);
        await team.InviteAsync((await access.GetAsync("oma", "oma-1"))!, "oma", new CreateInviteRequest
        {
            FullName = "Ada", Email = "ada@example.com", Grants = [new GrantDto { WorkspaceId = "oma-1", RoleId = "analyst" }],
        });
        return team;
    }

    // Someone registered the address without proving it (verification off, or never
    // verified). The link reached the real inbox, so its owner sets the login, and
    // whoever registered first cannot keep them out.
    [Fact]
    public async Task AnUnprovedAccountUnderTheAddress_IsClaimedFromTheLink()
    {
        ApplicationUser squatted = Account("ada-squat", "ada@example.com", proved: false);
        TeamService team = await InviteAdaAsync();
        Assert.False((await team.PreviewInviteAsync(Token())).HasAccount);

        InviteSignUpService sut = new(team, _auth.Object, _users.Object, Mock.Of<IAppLogger<InviteSignUpService>>());
        await sut.SignUpAndAcceptAsync(Token(), new InviteSignUpRequest { FirstName = "Ada", Password = "Str0ng!pass" });

        Assert.True(squatted.EmailConfirmed);
        Assert.Equal("Ada", squatted.FirstName);
        _users.Verify(u => u.ResetPasswordAsync(squatted, "reset", "Str0ng!pass"), Times.Once);
        _users.Verify(u => u.CreateAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()), Times.Never);
        Assert.Equal("ada-squat", Assert.Single(_team.Grants).UserId);
    }

    // Accepting signed in proves the address too: the link came from its inbox.
    [Fact]
    public async Task AcceptingSignedIn_MarksTheAddressProved()
    {
        ApplicationUser ada = Account("ada", "ada@example.com", proved: false);
        TeamService team = await InviteAdaAsync();

        await team.AcceptInviteAsync("ada", Token());

        Assert.True(ada.EmailConfirmed);
    }
}
