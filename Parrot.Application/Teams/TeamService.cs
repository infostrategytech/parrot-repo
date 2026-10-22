using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Parrot.Application.DTOs.Teams;
using Parrot.Application.Email;
using Parrot.Application.Logging;
using Parrot.Application.Models;
using Parrot.Application.Workspaces;
using Parrot.Domain.Entities;
using Parrot.Domain.Exceptions;

namespace Parrot.Application.Teams;

/// <summary>
/// Running an owner's team: who is in it, what each person may do in which
/// workspaces and on which channels, and the invitations that bring new
/// people in. Every change is made by the owner, from a session in any of
/// their workspaces; accepting an invite is made by the person invited.
/// </summary>
public interface ITeamService
{
    /// <summary>The built-in roles, then the owner's own, with permissions and channels to choose from.</summary>
    Task<TeamRolesResponse> GetRolesAsync(WorkspaceAccess access);

    Task<RoleResponse> CreateRoleAsync(WorkspaceAccess access, SaveRoleRequest request);

    /// <summary>A copy of any role, built-in or the owner's, as a new role of the owner's own: "Name (copy)".</summary>
    Task<RoleResponse> DuplicateRoleAsync(WorkspaceAccess access, string roleId);

    /// <summary>Only the owner's own roles. Everyone holding it gets the change on their next request.</summary>
    Task<RoleResponse> UpdateRoleAsync(WorkspaceAccess access, string roleId, SaveRoleRequest request);

    /// <summary>Only the owner's own roles, and only while nobody holds it and no pending invite names it.</summary>
    Task DeleteRoleAsync(WorkspaceAccess access, string roleId);

    Task<TeamResponse> GetTeamAsync(WorkspaceAccess access);

    Task<TeamInviteResponse> InviteAsync(WorkspaceAccess access, string inviterUserId, CreateInviteRequest request);

    Task<TeamInviteResponse> ResendInviteAsync(WorkspaceAccess access, string inviterUserId, Guid inviteId);

    Task RevokeInviteAsync(WorkspaceAccess access, Guid inviteId);

    Task<TeamMemberResponse> UpdateMemberAsync(WorkspaceAccess access, string memberUserId, UpdateMemberRequest request);

    Task RemoveMemberAsync(WorkspaceAccess access, string memberUserId);

    Task<InvitePreviewResponse> PreviewInviteAsync(string token);

    /// <summary>The invite behind a link, while it can still be accepted. Otherwise not found or a conflict saying why.</summary>
    Task<TeamInvite> GetPendingInviteAsync(string token);

    /// <summary>
    /// Gives the user the invite's grants and returns the workspace to start in.
    /// The user's email has to be the one invited.
    /// </summary>
    Task<string> AcceptInviteAsync(string userId, string token);
}

public sealed class TeamService : ITeamService
{
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromDays(7);

    private const string AllWorkspaces = "All workspaces";

    private readonly ITeamRepository _team;
    private readonly IWorkspaceRepository _workspaces;
    private readonly IWorkspaceAccessService _access;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IEmailService _email;
    private readonly EmailSettings _emailSettings;
    private readonly IWebHostEnvironment _env;
    private readonly InviteTokens _tokens;
    private readonly TimeProvider _clock;
    private readonly IAppLogger<TeamService> _logger;

    public TeamService(
        ITeamRepository team,
        IWorkspaceRepository workspaces,
        IWorkspaceAccessService access,
        UserManager<ApplicationUser> userManager,
        IEmailService email,
        IOptions<EmailSettings> emailSettings,
        IWebHostEnvironment env,
        InviteTokens tokens,
        TimeProvider clock,
        IAppLogger<TeamService> logger)
    {
        _team = team;
        _workspaces = workspaces;
        _access = access;
        _userManager = userManager;
        _email = email;
        _emailSettings = emailSettings.Value;
        _env = env;
        _tokens = tokens;
        _clock = clock;
        _logger = logger;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public async Task<TeamRolesResponse> GetRolesAsync(WorkspaceAccess access)
    {
        string ownerId = RequireTeamOwner(access);
        IReadOnlyList<WorkspaceGrant> grants = await _team.ListTeamGrantsAsync(ownerId);

        int Holders(Func<WorkspaceGrant, bool> holds) => grants.Where(holds).Select(g => g.UserId).Distinct().Count();

        List<RoleResponse> roles =
        [
            .. WorkspaceRoles.All.Select(r => new RoleResponse(r.Id, r.Name, false, r.Permissions, Holders(g => g.SystemRole == r.Id))),
            .. (await _team.ListRolesAsync(ownerId)).Select(r => ToResponse(r, Holders(g => g.CustomRoleId == r.Id))),
        ];

        return new TeamRolesResponse(
            roles,
            WorkspacePermissions.Grantable.Select(p => new OptionResponse(p.Key, p.Label)).ToList(),
            WorkspaceChannels.All.Select(c => new OptionResponse(c.Key, c.Label)).ToList());
    }

    public async Task<RoleResponse> CreateRoleAsync(WorkspaceAccess access, SaveRoleRequest request)
    {
        string ownerId = RequireTeamOwner(access);
        OwnerRole role = new()
        {
            OwnerId = ownerId,
            Name = ToRoleName(request.Name),
            NormalizedName = ToRoleName(request.Name).ToUpperInvariant(),
            Permissions = ToPermissions(request.Permissions),
            CreatedAt = Now,
        };

        if (!await _team.TryAddRoleAsync(role))
        {
            throw new ConflictException($"You already have a role called {role.Name}.");
        }

        using (_logger.BeginEntityScope("Team", ownerId))
        {
            _logger.LogInformation("Role {RoleId} created", role.Id);
        }

        return ToResponse(role, 0);
    }

    public async Task<RoleResponse> DuplicateRoleAsync(WorkspaceAccess access, string roleId)
    {
        string ownerId = RequireTeamOwner(access);
        string name;
        IReadOnlyList<string> permissions;
        if (WorkspaceRoles.FindById(roleId) is { } builtIn)
        {
            (name, permissions) = (builtIn.Name, builtIn.Permissions);
        }
        else
        {
            OwnerRole own = await GetOwnRoleOrThrowAsync(ownerId, roleId);
            (name, permissions) = (own.Name, own.Permissions);
        }

        // "Name (copy)", then "(copy 2)", and on, until one is free. The owner can rename it after.
        HashSet<string> taken = (await _team.ListRolesAsync(ownerId)).Select(r => r.NormalizedName).ToHashSet();
        string copy = $"{name} (copy)";
        for (int n = 2; taken.Contains(copy.ToUpperInvariant()); n++)
        {
            copy = $"{name} (copy {n})";
        }

        return await CreateRoleAsync(access, new SaveRoleRequest { Name = copy[..Math.Min(copy.Length, 64)], Permissions = permissions });
    }

    public async Task<RoleResponse> UpdateRoleAsync(WorkspaceAccess access, string roleId, SaveRoleRequest request)
    {
        string ownerId = RequireTeamOwner(access);
        OwnerRole role = await GetOwnRoleOrThrowAsync(ownerId, roleId);

        role.Name = ToRoleName(request.Name);
        role.NormalizedName = role.Name.ToUpperInvariant();
        role.Permissions = ToPermissions(request.Permissions);
        role.UpdatedAt = Now;

        if (!await _team.TryUpdateRoleAsync(role))
        {
            throw new ConflictException($"You already have a role called {role.Name}.");
        }

        // Everyone holding it gets the change on their next request.
        IReadOnlyList<string> holders = await _team.ListRoleHoldersAsync(ownerId, role.Id);
        foreach (string holder in holders)
        {
            await _access.ForgetAsync(ownerId, holder);
        }

        using (_logger.BeginEntityScope("Team", ownerId))
        {
            _logger.LogInformation("Role {RoleId} changed; {Holders} people hold it", role.Id, holders.Count);
        }

        return ToResponse(role, holders.Count);
    }

    public async Task DeleteRoleAsync(WorkspaceAccess access, string roleId)
    {
        string ownerId = RequireTeamOwner(access);
        OwnerRole role = await GetOwnRoleOrThrowAsync(ownerId, roleId);

        int holders = (await _team.ListRoleHoldersAsync(ownerId, role.Id)).Count;
        if (holders > 0)
        {
            throw new ConflictException(holders == 1
                ? $"1 person holds {role.Name}. Give them another role first."
                : $"{holders} people hold {role.Name}. Give them another role first.");
        }

        if ((await _team.ListPendingInvitesAsync(ownerId, Now)).Any(i => i.Grants.Any(g => g.CustomRoleId == role.Id)))
        {
            throw new ConflictException($"A pending invite gives {role.Name}. Withdraw it or wait until it is answered.");
        }

        await _team.DeleteRoleAsync(role);

        using (_logger.BeginEntityScope("Team", ownerId))
        {
            _logger.LogInformation("Role {RoleId} deleted", role.Id);
        }
    }

    public async Task<TeamResponse> GetTeamAsync(WorkspaceAccess access)
    {
        string ownerId = RequireTeamOwner(access);
        IReadOnlyList<Workspace> workspaces = await _workspaces.ListByOwnerAsync(ownerId);

        Dictionary<Guid, OwnerRole> roles = await RoleBookAsync(ownerId);

        List<TeamMemberResponse> members = [];
        if (await _userManager.FindByIdAsync(ownerId) is { } owner)
        {
            DateTime since = workspaces.Count > 0 ? workspaces[0].CreatedAt : owner.CreatedAt;
            members.Add(new TeamMemberResponse(owner.Id, FullName(owner), owner.Email!, true, [], since));
        }

        foreach (IGrouping<string, WorkspaceGrant> person in (await _team.ListTeamGrantsAsync(ownerId)).GroupBy(g => g.UserId))
        {
            // An account deleted since it joined has nothing left to show.
            if (await _userManager.FindByIdAsync(person.Key) is { } user)
            {
                members.Add(ToResponse(user, person.ToList(), roles));
            }
        }

        IReadOnlyList<TeamInvite> invites = await _team.ListPendingInvitesAsync(ownerId, Now);
        return new TeamResponse(
            members,
            invites.Select(i => ToResponse(i, roles)).ToList(),
            workspaces.Select(w => new TeamWorkspaceResponse(w.Id, w.Name, w.LogoUrl)).ToList());
    }

    public async Task<TeamInviteResponse> InviteAsync(WorkspaceAccess access, string inviterUserId, CreateInviteRequest request)
    {
        string ownerId = RequireTeamOwner(access);

        string fullName = request.FullName.Trim();
        string email = request.Email.Trim();
        if (fullName.Length is < 1 or > 80)
        {
            throw new ValidationException("Enter their full name.");
        }

        if (email.Length == 0)
        {
            throw new ValidationException("Enter their email address.");
        }

        string normalizedEmail = _userManager.NormalizeEmail(email);
        List<GrantSpec> grants = await ToGrantSpecsAsync(ownerId, request.Grants);

        if (await _userManager.FindByEmailAsync(email) is { } existing)
        {
            if (existing.Id == ownerId)
            {
                throw new ConflictException($"{email} owns these workspaces.");
            }

            if ((await _team.ListGrantsAsync(ownerId, existing.Id)).Count > 0)
            {
                throw new ConflictException($"{email} is already in your team. Change their access instead.");
            }
        }

        if (await _team.FindPendingInviteAsync(ownerId, normalizedEmail, Now) is not null)
        {
            throw new ConflictException($"{email} has already been invited. Resend the invite instead.");
        }

        Guid inviteId = Guid.NewGuid();
        DateTime expiresAt = Now + InviteLifetime;
        string token = _tokens.Issue(inviteId, expiresAt);
        TeamInvite invite = new()
        {
            Id = inviteId,
            OwnerId = ownerId,
            Email = email,
            NormalizedEmail = normalizedEmail,
            FullName = fullName,
            Grants = grants,
            TokenHash = Hash(token),
            InvitedByUserId = inviterUserId,
            CreatedAt = Now,
            ExpiresAt = expiresAt,
        };

        // Sent before it is saved: an invite nobody received would only block
        // inviting the same address again.
        await SendInviteAsync(invite, token, inviterUserId);
        await _team.AddInviteAsync(invite);

        using (_logger.BeginEntityScope("Team", ownerId))
        {
            _logger.LogInformation("Invite {InviteId} sent with {GrantCount} grants", invite.Id, grants.Count);
        }

        return ToResponse(invite, await RoleBookAsync(ownerId));
    }

    public async Task<TeamInviteResponse> ResendInviteAsync(WorkspaceAccess access, string inviterUserId, Guid inviteId)
    {
        string ownerId = RequireTeamOwner(access);
        TeamInvite invite = await GetInviteOrThrowAsync(ownerId, inviteId);
        if (invite.AcceptedAt is not null || invite.RevokedAt is not null)
        {
            throw new ConflictException("That invite has already been answered or withdrawn.");
        }

        // A new token with a fresh week, so the link in the earlier email stops working.
        invite.ExpiresAt = Now + InviteLifetime;
        string token = _tokens.Issue(invite.Id, invite.ExpiresAt);
        invite.TokenHash = Hash(token);

        await SendInviteAsync(invite, token, inviterUserId);
        await _team.UpdateInviteAsync(invite);

        using (_logger.BeginEntityScope("Team", ownerId))
        {
            _logger.LogInformation("Invite {InviteId} resent", invite.Id);
        }

        return ToResponse(invite, await RoleBookAsync(ownerId));
    }

    public async Task RevokeInviteAsync(WorkspaceAccess access, Guid inviteId)
    {
        string ownerId = RequireTeamOwner(access);
        TeamInvite invite = await GetInviteOrThrowAsync(ownerId, inviteId);
        if (invite.AcceptedAt is not null)
        {
            throw new ConflictException("That invite has already been accepted. Remove the member instead.");
        }

        if (invite.RevokedAt is null)
        {
            invite.RevokedAt = Now;
            await _team.UpdateInviteAsync(invite);
        }

        using (_logger.BeginEntityScope("Team", ownerId))
        {
            _logger.LogInformation("Invite {InviteId} withdrawn", invite.Id);
        }
    }

    public async Task<TeamMemberResponse> UpdateMemberAsync(WorkspaceAccess access, string memberUserId, UpdateMemberRequest request)
    {
        string ownerId = RequireTeamOwner(access);
        ApplicationUser user = await GetMemberOrThrowAsync(ownerId, memberUserId);
        List<GrantSpec> specs = await ToGrantSpecsAsync(ownerId, request.Grants);

        IReadOnlyList<WorkspaceGrant> held = await _team.ListGrantsAsync(ownerId, memberUserId);
        string? invitedBy = held.Select(g => g.InvitedByUserId).FirstOrDefault(id => id is not null);
        DateTime joined = held.Min(g => g.CreatedAt);
        List<WorkspaceGrant> grants = specs.Select(s => ToGrant(ownerId, memberUserId, s, invitedBy, joined)).ToList();

        await _team.ReplaceGrantsAsync(ownerId, memberUserId, grants);
        await _access.ForgetAsync(ownerId, memberUserId);

        using (_logger.BeginEntityScope("Team", ownerId))
        {
            _logger.LogInformation("Member {UserId} given {GrantCount} grants", memberUserId, grants.Count);
        }

        return ToResponse(user, grants, await RoleBookAsync(ownerId));
    }

    public async Task RemoveMemberAsync(WorkspaceAccess access, string memberUserId)
    {
        string ownerId = RequireTeamOwner(access);
        await GetMemberOrThrowAsync(ownerId, memberUserId);

        await _team.ReplaceGrantsAsync(ownerId, memberUserId, []);
        await _access.ForgetAsync(ownerId, memberUserId);

        using (_logger.BeginEntityScope("Team", ownerId))
        {
            _logger.LogInformation("Member {UserId} removed", memberUserId);
        }
    }

    public async Task<InvitePreviewResponse> PreviewInviteAsync(string token)
    {
        TeamInvite invite = await FindByTokenOrThrowAsync(token);
        IReadOnlyList<Workspace> workspaces = await _workspaces.ListByOwnerAsync(invite.OwnerId);
        ApplicationUser? inviter = await _userManager.FindByIdAsync(invite.InvitedByUserId);
        Workspace? first = workspaces.FirstOrDefault();

        return new InvitePreviewResponse(
            first?.Name ?? "A team on OmniAI",
            first?.LogoUrl,
            inviter is null ? "Your team" : FullName(inviter) ?? inviter.Email!,
            invite.Email,
            WorkspaceNames(invite.Grants, workspaces),
            invite.ExpiresAt,
            StatusOf(invite),
            // Only a proved login signs in here. One never proved (made while
            // verification was off, or never verified) is set up from the link
            // instead, so whoever registered the address first cannot keep its
            // owner out.
            await _userManager.FindByEmailAsync(invite.Email) is { EmailConfirmed: true },
            invite.FullName);
    }

    public async Task<TeamInvite> GetPendingInviteAsync(string token)
    {
        TeamInvite invite = await FindByTokenOrThrowAsync(token);
        return StatusOf(invite) switch
        {
            "pending" => invite,
            "accepted" => throw new ConflictException("This invite has already been used. Sign in to reach the team."),
            "revoked" => throw new ConflictException("This invite was withdrawn. Ask the owner for a new one."),
            _ => throw new ConflictException("This invite has expired. Ask the owner for a new one."),
        };
    }

    public async Task<string> AcceptInviteAsync(string userId, string token)
    {
        TeamInvite invite = await FindByTokenOrThrowAsync(token);
        ApplicationUser user = await _userManager.FindByIdAsync(userId)
            ?? throw new NotFoundException("Account not found.");

        if (invite.AcceptedAt is not null && invite.AcceptedByUserId != userId)
        {
            throw new ConflictException("This invite has already been used.");
        }

        if (invite.AcceptedAt is null)
        {
            if (invite.RevokedAt is not null)
            {
                throw new ConflictException("This invite was withdrawn. Ask the owner for a new one.");
            }

            if (invite.ExpiresAt <= Now)
            {
                throw new ConflictException("This invite has expired. Ask the owner for a new one.");
            }

            // The link alone is not enough: it has to be opened by the account it was sent to.
            if (user.NormalizedEmail != invite.NormalizedEmail)
            {
                throw new ForbiddenException($"This invite was sent to {invite.Email}. Sign in with that address to accept it.");
            }

            if (invite.OwnerId == userId)
            {
                throw new ConflictException("You own these workspaces already.");
            }

            // The link reached this address's inbox and was opened signed in as it: that proves it.
            if (!user.EmailConfirmed)
            {
                user.EmailConfirmed = true;
                await _userManager.UpdateAsync(user);
            }

            await GrantAsync(invite, userId);
        }

        // Start in the first workspace the invite reaches that still exists.
        foreach (Workspace workspace in await _workspaces.ListByOwnerAsync(invite.OwnerId))
        {
            if (await _access.GetAsync(userId, workspace.Id) is not null)
            {
                return workspace.Id;
            }
        }

        throw new NotFoundException("The workspaces this invite was for no longer exist.");
    }

    // Grants for workspaces the invite names replace the ones held there;
    // the rest of what the person already holds in this team is kept.
    private async Task GrantAsync(TeamInvite invite, string userId)
    {
        IReadOnlyList<WorkspaceGrant> held = await _team.ListGrantsAsync(invite.OwnerId, userId);
        HashSet<string?> named = invite.Grants.Select(g => g.WorkspaceId).ToHashSet();

        List<WorkspaceGrant> grants =
        [
            .. held.Where(g => !named.Contains(g.WorkspaceId)),
            .. invite.Grants.Select(s => ToGrant(invite.OwnerId, userId, s, invite.InvitedByUserId, Now)),
        ];

        await _team.ReplaceGrantsAsync(invite.OwnerId, userId, grants);

        invite.AcceptedAt = Now;
        invite.AcceptedByUserId = userId;
        await _team.UpdateInviteAsync(invite);
        await _access.ForgetAsync(invite.OwnerId, userId);

        using (_logger.BeginEntityScope("Team", invite.OwnerId))
        {
            _logger.LogInformation("Invite {InviteId} accepted by {UserId}", invite.Id, userId);
        }
    }

    // Only the owner runs the team. The filter in front of every team route
    // checks the same permission; the service does not rely on it.
    private static string RequireTeamOwner(WorkspaceAccess access) =>
        access.Can(WorkspacePermissions.TeamManage)
            ? access.Workspace.OwnerId
            : throw new ForbiddenException("Only the workspace owner can manage the team.");

    private async Task<List<GrantSpec>> ToGrantSpecsAsync(string ownerId, IReadOnlyList<GrantDto>? requested)
    {
        if (requested is null || requested.Count == 0)
        {
            throw new ValidationException("Give them access to at least one workspace.");
        }

        if (requested.Count(g => g.WorkspaceId is null) > 1)
        {
            throw new ValidationException("Give at most one set of access for all workspaces.");
        }

        List<string> named = requested.Where(g => g.WorkspaceId is not null).Select(g => g.WorkspaceId!).ToList();
        if (named.Count != named.Distinct(StringComparer.Ordinal).Count())
        {
            throw new ValidationException("Each workspace can be given access only once.");
        }

        HashSet<string> owned = (await _workspaces.ListByOwnerAsync(ownerId)).Select(w => w.Id).ToHashSet();
        if (named.Any(id => !owned.Contains(id)))
        {
            throw new ValidationException("Choose workspaces from your own.");
        }

        // A grant names its role and channels; the role decides what it allows.
        Dictionary<Guid, OwnerRole> roles = await RoleBookAsync(ownerId);
        return requested.Select(g =>
        {
            if (WorkspaceRoles.FindById(g.RoleId) is { } builtIn)
            {
                return new GrantSpec { WorkspaceId = g.WorkspaceId, SystemRole = builtIn.Id, Channels = ToChannels(g.Channels) };
            }

            return Guid.TryParse(g.RoleId, out Guid id) && roles.ContainsKey(id)
                ? new GrantSpec { WorkspaceId = g.WorkspaceId, CustomRoleId = id, Channels = ToChannels(g.Channels) }
                : throw new ValidationException("Choose one of the listed roles.");
        }).ToList();
    }

    private static List<string> ToPermissions(IReadOnlyList<string>? requested)
    {
        if (requested is null || requested.Count == 0)
        {
            throw new ValidationException("Give the role at least one permission.");
        }

        string? unknown = requested.FirstOrDefault(p => !WorkspacePermissions.IsGrantable(p));
        if (unknown is not null)
        {
            throw new ValidationException($"\"{unknown}\" is not a permission a member can be given.");
        }

        // In the form's order, once each.
        return WorkspacePermissions.Grantable.Select(p => p.Key).Where(requested.Contains).ToList();
    }

    private static string ToRoleName(string? value)
    {
        string name = (value ?? "").Trim();
        if (name.Length is < 1 or > 64)
        {
            throw new ValidationException("A role name must be between 1 and 64 characters.");
        }

        // "Owner" is how the owner is shown everywhere; a role by that name would pass for them.
        return string.Equals(name, WorkspaceRoles.Owner, StringComparison.OrdinalIgnoreCase)
            ? throw new ValidationException("\"Owner\" is reserved for the workspace owner. Choose another name.")
            : name;
    }

    // Built-in roles are not the owner's to change: they are the same in every business.
    private async Task<OwnerRole> GetOwnRoleOrThrowAsync(string ownerId, string roleId)
    {
        if (WorkspaceRoles.FindById(roleId) is not null)
        {
            throw new ForbiddenException("Built-in roles cannot be changed. Duplicate it to make a role of your own.");
        }

        return Guid.TryParse(roleId, out Guid id) && await _team.GetRoleAsync(ownerId, id) is { } role
            ? role
            : throw new NotFoundException("Role not found.");
    }

    private async Task<Dictionary<Guid, OwnerRole>> RoleBookAsync(string ownerId) =>
        (await _team.ListRolesAsync(ownerId)).ToDictionary(r => r.Id);

    private static List<string> ToChannels(IReadOnlyList<string>? requested)
    {
        if (requested is null)
        {
            return [];
        }

        string? unknown = requested.FirstOrDefault(c => !WorkspaceChannels.IsKnown(c));
        return unknown is null
            ? WorkspaceChannels.All.Select(c => c.Key).Where(requested.Contains).ToList()
            : throw new ValidationException($"\"{unknown}\" is not a channel.");
    }

    private async Task SendInviteAsync(TeamInvite invite, string token, string inviterUserId)
    {
        string link = $"{_emailSettings.BaseUrl.TrimEnd('/')}/invite/{token}";

        if (_env.IsDevelopment() && !_emailSettings.SendInDevelopment)
        {
            // Nothing is sent locally, so the link is the only way to try the flow.
            _logger.LogInformation("Invite email skipped in Development. Open {InviteLink} to accept it.", link);
            return;
        }

        IReadOnlyList<Workspace> workspaces = await _workspaces.ListByOwnerAsync(invite.OwnerId);
        ApplicationUser? inviter = await _userManager.FindByIdAsync(inviterUserId);
        string inviterName = inviter is null ? "Your team" : FullName(inviter) ?? inviter.Email!;
        string teamName = workspaces.FirstOrDefault()?.Name ?? "their team";
        string where = string.Join(", ", WorkspaceNames(invite.Grants, workspaces));

        // Every value a person typed is encoded: a workspace name is not markup.
        string body = $"""
            <h2>Join {WebUtility.HtmlEncode(teamName)} on OmniAI</h2>
            <p>Hi {WebUtility.HtmlEncode(invite.FullName)},</p>
            <p>{WebUtility.HtmlEncode(inviterName)} has invited you to work in {WebUtility.HtmlEncode(where)}.</p>
            <p><a href="{WebUtility.HtmlEncode(link)}">Accept the invite</a></p>
            <p>Accept to sign in with {WebUtility.HtmlEncode(invite.Email)}, or to set up your login if you do not have one yet. The link works for 7 days, until {invite.ExpiresAt:d MMMM yyyy}.</p>
            """;

        try
        {
            await _email.SendEmailAsync(invite.Email, $"Join {teamName} on OmniAI", body);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Invite email could not be sent for team {OwnerId}", invite.OwnerId);
            throw new ServiceUnavailableException("We could not send the invite email. Try again in a moment.", ex);
        }
    }

    private static List<string> WorkspaceNames(IEnumerable<GrantSpec> grants, IReadOnlyList<Workspace> workspaces) =>
        grants.Select(g => g.WorkspaceId is null
                ? AllWorkspaces
                : workspaces.FirstOrDefault(w => w.Id == g.WorkspaceId)?.Name ?? "A workspace")
            .ToList();

    // Ours (signed, for the invite audience) and still the link the invite was
    // last sent with: a resent invite's earlier link no longer matches its hash.
    private async Task<TeamInvite> FindByTokenOrThrowAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || _tokens.Read(token) is not { } inviteId
            || await _team.FindInviteByTokenHashAsync(Hash(token)) is not { } invite || invite.Id != inviteId)
        {
            throw new NotFoundException("This invite link is not valid.");
        }

        return invite;
    }

    private async Task<TeamInvite> GetInviteOrThrowAsync(string ownerId, Guid inviteId) =>
        await _team.GetInviteAsync(ownerId, inviteId) ?? throw new NotFoundException("Invite not found.");

    private async Task<ApplicationUser> GetMemberOrThrowAsync(string ownerId, string userId)
    {
        if (userId == ownerId)
        {
            throw new ForbiddenException("The owner cannot be changed or removed.");
        }

        if ((await _team.ListGrantsAsync(ownerId, userId)).Count == 0)
        {
            throw new NotFoundException("Member not found.");
        }

        return await _userManager.FindByIdAsync(userId) ?? throw new NotFoundException("Member not found.");
    }

    private string StatusOf(TeamInvite invite) =>
        invite.AcceptedAt is not null ? "accepted"
        : invite.RevokedAt is not null ? "revoked"
        : invite.ExpiresAt <= Now ? "expired"
        : "pending";

    private static WorkspaceGrant ToGrant(string ownerId, string userId, GrantSpec spec, string? invitedBy, DateTime createdAt) => new()
    {
        OwnerId = ownerId,
        UserId = userId,
        WorkspaceId = spec.WorkspaceId,
        SystemRole = spec.SystemRole,
        CustomRoleId = spec.CustomRoleId,
        Channels = [.. spec.Channels],
        InvitedByUserId = invitedBy,
        CreatedAt = createdAt,
    };

    /// <summary>How a link's token is stored and looked up: hex SHA-256.</summary>
    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    private static string? FullName(ApplicationUser user)
    {
        string name = $"{user.FirstName} {user.LastName}".Trim();
        return name.Length == 0 ? null : name;
    }

    private static GrantResponse ToGrantResponse(string? workspaceId, string? systemRole, Guid? customRoleId, IReadOnlyList<string> channels,
        IReadOnlyDictionary<Guid, OwnerRole> roles)
    {
        if (customRoleId is { } id)
        {
            return new GrantResponse(workspaceId, id.ToString(), roles.GetValueOrDefault(id)?.Name ?? "Removed role", true, channels);
        }

        return new GrantResponse(workspaceId, systemRole ?? "", WorkspaceRoles.FindById(systemRole)?.Name ?? "Removed role", false, channels);
    }

    private static RoleResponse ToResponse(OwnerRole role, int holders) =>
        new(role.Id.ToString(), role.Name, true, role.Permissions, holders);

    private static TeamMemberResponse ToResponse(ApplicationUser user, IReadOnlyList<WorkspaceGrant> grants, IReadOnlyDictionary<Guid, OwnerRole> roles) =>
        new(user.Id, FullName(user), user.Email!, false,
            grants.Select(g => ToGrantResponse(g.WorkspaceId, g.SystemRole, g.CustomRoleId, g.Channels, roles)).ToList(),
            grants.Min(g => g.CreatedAt));

    private static TeamInviteResponse ToResponse(TeamInvite invite, IReadOnlyDictionary<Guid, OwnerRole> roles) =>
        new(invite.Id, invite.FullName, invite.Email,
            invite.Grants.Select(g => ToGrantResponse(g.WorkspaceId, g.SystemRole, g.CustomRoleId, g.Channels, roles)).ToList(),
            invite.CreatedAt, invite.ExpiresAt);
}
