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

public class TeamServiceTests
{
    private const string Owner = "owner-1";

    private readonly FakeWorkspaceRepository _workspaces = new();
    private readonly FakeTeamRepository _team = new();
    private readonly Mock<UserManager<ApplicationUser>> _users;
    private readonly Mock<IEmailService> _email = new();
    private readonly WorkspaceAccessService _access;
    private readonly Dictionary<string, ApplicationUser> _accounts = [];
    private string? _sentBody;

    public TeamServiceTests()
    {
        _workspaces.Seed("ws-1", Owner, name: "Bakery", createdAt: DateTime.UtcNow.AddDays(-2));
        _workspaces.Seed("ws-2", Owner, name: "Catering", createdAt: DateTime.UtcNow.AddDays(-1));
        _workspaces.Seed("theirs", "someone-else", name: "Not yours");
        _access = TeamFixture.Access(_workspaces, _team);

        _users = new Mock<UserManager<ApplicationUser>>(Mock.Of<IUserStore<ApplicationUser>>(), null!, null!, null!, null!, null!, null!, null!, null!);
        _users.Setup(u => u.NormalizeEmail(It.IsAny<string>())).Returns<string>(e => e.ToUpperInvariant());
        _users.Setup(u => u.FindByIdAsync(It.IsAny<string>())).ReturnsAsync((string id) => _accounts.GetValueOrDefault(id));
        _users.Setup(u => u.FindByEmailAsync(It.IsAny<string>()))
            .ReturnsAsync((string email) => _accounts.Values.FirstOrDefault(a => a.NormalizedEmail == email.ToUpperInvariant()));
        Account(Owner, "owner@example.com", "Oma");
        _email.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string, string, string>((_, _, html, _) => _sentBody = html)
            .Returns(Task.CompletedTask);
    }

    private ApplicationUser Account(string id, string email, string? firstName = null)
    {
        ApplicationUser user = new() { Id = id, Email = email, NormalizedEmail = email.ToUpperInvariant(), FirstName = firstName };
        _accounts[id] = user;
        return user;
    }

    private TeamService Sut()
    {
        Mock<IWebHostEnvironment> env = new();
        env.SetupGet(e => e.EnvironmentName).Returns("Production");
        EmailSettings settings = new()
        {
            SmtpHost = "smtp", SmtpUser = "u", SmtpPassword = "p", FromAddress = "f@example.com", FromName = "OmniAI",
            BaseUrl = "https://app.example.com",
        };
        return new TeamService(_team, _workspaces, _access, _users.Object, _email.Object, Options.Create(settings), env.Object, TeamFixture.InviteTokens(),
            TimeProvider.System, Mock.Of<IAppLogger<TeamService>>());
    }

    private async Task<WorkspaceAccess> OwnerAccess() => (await _access.GetAsync(Owner, "ws-1"))!;

    private static GrantDto Grant(string? workspaceId, string role = WorkspaceRoles.ConversationManager, IReadOnlyList<string>? channels = null) =>
        new() { WorkspaceId = workspaceId, RoleId = WorkspaceRoles.FindByName(role)?.Id ?? role, Channels = channels };

    private static CreateInviteRequest Invite(string email, params GrantDto[] grants) =>
        new() { FullName = "Ada Obi", Email = email, Grants = grants };

    // The token only ever travels in the email; the link in it is the one that works.
    private string TokenFromEmail()
    {
        Assert.NotNull(_sentBody);
        int start = _sentBody.IndexOf("/invite/", StringComparison.Ordinal) + "/invite/".Length;
        int end = _sentBody.IndexOf('"', start);
        return _sentBody[start..end];
    }

    [Fact]
    public async Task AnInvite_IsEmailed_AndOnlyTheTokensHashIsKept()
    {
        await Sut().InviteAsync(await OwnerAccess(), Owner, Invite("ada@example.com", Grant("ws-1", channels: [WorkspaceChannels.WhatsApp])));

        TeamInvite invite = Assert.Single(_team.Invites);
        string token = TokenFromEmail();
        Assert.NotEqual(token, invite.TokenHash);
        Assert.Equal(TeamService.Hash(token), invite.TokenHash);
        Assert.Equal([WorkspaceChannels.WhatsApp], invite.Grants[0].Channels);
        // The grant names its role; the role decides the permissions.
        Assert.Equal("conversation-manager", invite.Grants[0].SystemRole);
        Assert.Null(invite.Grants[0].CustomRoleId);
    }

    [Fact]
    public async Task TheEmail_EncodesWhatPeopleTyped()
    {
        _workspaces.Rows["ws-1"].Name = "<script>x</script>";
        await Sut().InviteAsync(await OwnerAccess(), Owner, Invite("ada@example.com", Grant("ws-1")));

        Assert.DoesNotContain("<script>", _sentBody);
        Assert.Contains("&lt;script&gt;", _sentBody);
    }

    [Theory]
    [InlineData("theirs")]
    [InlineData("missing")]
    public async Task AnInvite_CannotNameAnotherOwnersWorkspace(string workspaceId)
    {
        WorkspaceAccess owner = await OwnerAccess();
        await Assert.ThrowsAsync<ValidationException>(() =>
            Sut().InviteAsync(owner, Owner, Invite("ada@example.com", Grant(workspaceId))));
        Assert.Empty(_team.Invites);
    }

    [Fact]
    public async Task AnInvite_IsRefused_ForTwoAllWorkspaceGrants_ARepeatedWorkspace_OrUnknownKeys()
    {
        WorkspaceAccess owner = await OwnerAccess();
        await Assert.ThrowsAsync<ValidationException>(() => Sut().InviteAsync(owner, Owner, Invite("a@example.com", Grant(null), Grant(null))));
        await Assert.ThrowsAsync<ValidationException>(() => Sut().InviteAsync(owner, Owner, Invite("a@example.com", Grant("ws-1"), Grant("ws-1"))));
        await Assert.ThrowsAsync<ValidationException>(() => Sut().InviteAsync(owner, Owner, Invite("a@example.com", Grant("ws-1", role: "Boss"))));
        await Assert.ThrowsAsync<ValidationException>(() => Sut().InviteAsync(owner, Owner, Invite("a@example.com", Grant("ws-1", channels: ["sms"]))));
        await Assert.ThrowsAsync<ValidationException>(() => Sut().InviteAsync(owner, Owner, Invite("a@example.com")));
    }

    [Fact]
    public async Task OnlyTheOwner_RunsTheTeam()
    {
        Account("ada", "ada@example.com");
        _team.Grant(Owner, "ada", null, WorkspaceRoles.ConversationManager);
        WorkspaceAccess member = (await _access.GetAsync("ada", "ws-1"))!;

        await Assert.ThrowsAsync<ForbiddenException>(() => Sut().InviteAsync(member, "ada", Invite("bob@example.com", Grant("ws-1"))));
        await Assert.ThrowsAsync<ForbiddenException>(() => Sut().GetTeamAsync(member));
    }

    [Fact]
    public async Task Accepting_GivesTheGrants_AndStartsInTheFirstWorkspaceTheyReach()
    {
        Account("ada", "Ada@Example.com");
        await Sut().InviteAsync(await OwnerAccess(), Owner, Invite("ada@example.com",
            Grant("ws-2", channels: [WorkspaceChannels.Instagram])));

        string workspaceId = await Sut().AcceptInviteAsync("ada", TokenFromEmail());

        Assert.Equal("ws-2", workspaceId);
        WorkspaceGrant grant = Assert.Single(_team.Grants);
        Assert.Equal(("ws-2", "ada", Owner), (grant.WorkspaceId, grant.UserId, grant.OwnerId));
        Assert.Equal([WorkspaceChannels.Instagram], grant.Channels);
        Assert.NotNull(_team.Invites[0].AcceptedAt);
    }

    [Fact]
    public async Task Accepting_FromAnotherAddress_IsRefused()
    {
        Account("mallory", "mallory@example.com");
        await Sut().InviteAsync(await OwnerAccess(), Owner, Invite("ada@example.com", Grant("ws-1")));

        await Assert.ThrowsAsync<ForbiddenException>(() => Sut().AcceptInviteAsync("mallory", TokenFromEmail()));
        Assert.Empty(_team.Grants);
    }

    [Fact]
    public async Task AWithdrawnOrResentLink_NoLongerWorks()
    {
        Account("ada", "ada@example.com");
        await Sut().InviteAsync(await OwnerAccess(), Owner, Invite("ada@example.com", Grant("ws-1")));
        string first = TokenFromEmail();

        await Sut().ResendInviteAsync(await OwnerAccess(), Owner, _team.Invites[0].Id);
        await Assert.ThrowsAsync<NotFoundException>(() => Sut().AcceptInviteAsync("ada", first));

        await Sut().RevokeInviteAsync(await OwnerAccess(), _team.Invites[0].Id);
        await Assert.ThrowsAsync<ConflictException>(() => Sut().AcceptInviteAsync("ada", TokenFromEmail()));
    }

    [Fact]
    public async Task AnExpiredLink_IsRefused()
    {
        Account("ada", "ada@example.com");
        await Sut().InviteAsync(await OwnerAccess(), Owner, Invite("ada@example.com", Grant("ws-1")));
        _team.Invites[0].ExpiresAt = DateTime.UtcNow.AddMinutes(-1);

        await Assert.ThrowsAsync<ConflictException>(() => Sut().AcceptInviteAsync("ada", TokenFromEmail()));
    }

    [Fact]
    public async Task RemovingAMember_TakesTheirAccessAwayAtOnce()
    {
        Account("ada", "ada@example.com");
        _team.Grant(Owner, "ada", null, WorkspaceRoles.ConversationManager);
        Assert.NotNull(await _access.GetAsync("ada", "ws-1"));

        await Sut().RemoveMemberAsync(await OwnerAccess(), "ada");

        // Not after the cache lifetime: the answer was forgotten with the grants.
        Assert.Null(await _access.GetAsync("ada", "ws-1"));
        Assert.Null(await _access.GetAsync("ada", "ws-2"));
    }

    [Fact]
    public async Task ChangingAMember_ReplacesTheirGrants_AndTakesEffectAtOnce()
    {
        Account("ada", "ada@example.com");
        _team.Grant(Owner, "ada", null, WorkspaceRoles.ConversationManager);
        Assert.NotNull(await _access.GetAsync("ada", "ws-2"));

        await Sut().UpdateMemberAsync(await OwnerAccess(), "ada", new UpdateMemberRequest { Grants = [Grant("ws-1", WorkspaceRoles.Analyst)] });

        Assert.Equal(WorkspaceRoles.Analyst, (await _access.GetAsync("ada", "ws-1"))!.Role);
        Assert.Null(await _access.GetAsync("ada", "ws-2"));
    }

    [Fact]
    public async Task TheOwner_CannotBeChangedOrRemoved()
    {
        WorkspaceAccess owner = await OwnerAccess();
        await Assert.ThrowsAsync<ForbiddenException>(() => Sut().RemoveMemberAsync(owner, Owner));
    }

    [Fact]
    public async Task TheTeam_ListsTheOwnerFirst_ThenMembersWithTheirGrants_AndPendingInvites()
    {
        Account("ada", "ada@example.com", "Ada");
        _team.Grant(Owner, "ada", "ws-1", WorkspaceRoles.ConversationManager, [WorkspaceChannels.WhatsApp]);
        await Sut().InviteAsync(await OwnerAccess(), Owner, Invite("bob@example.com", Grant(null, WorkspaceRoles.Analyst)));

        TeamResponse team = await Sut().GetTeamAsync(await OwnerAccess());

        Assert.Equal([Owner, "ada"], team.Members.Select(m => m.UserId));
        Assert.True(team.Members[0].IsOwner);
        Assert.Equal([WorkspaceChannels.WhatsApp], team.Members[1].Grants[0].Channels);
        Assert.Equal("bob@example.com", Assert.Single(team.Invites).Email);
        Assert.Equal(["ws-1", "ws-2"], team.Workspaces.Select(w => w.Id));
    }
}
