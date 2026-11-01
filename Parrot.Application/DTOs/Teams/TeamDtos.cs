using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Parrot.Application.DTOs.Teams;

[Description("What a person may do in one workspace, or in all of the owner's workspaces: a role, and the channels they see.")]
public record GrantDto
{
    [Description("The workspace's id, or null for every workspace the owner has or makes later. A workspace's own grant is used there instead of the all-workspaces one.")]
    public string? WorkspaceId { get; init; }

    [Required(ErrorMessage = "Choose a role.")]
    [Description("A role id from GET /api/team/roles: a built-in one such as \"analyst\", or one of the owner's own. The role decides the permissions.")]
    public string RoleId { get; init; } = string.Empty;

    [Description("Channel keys from GET /api/team/roles, e.g. [\"whatsapp\"]. Leave it out or empty for every channel.")]
    public IReadOnlyList<string>? Channels { get; init; }
}

[Description("A grant as the Team page shows it.")]
public record GrantResponse(
    string? WorkspaceId,
    string RoleId,
    string RoleName,
    [property: Description("True for one of the owner's own roles, which the page marks Custom.")]
    bool IsCustomRole,
    IReadOnlyList<string> Channels
);

[Description("Someone in the owner's team, with what they may do in each workspace.")]
public record TeamMemberResponse(
    [property: Description("The person's user id. Use it to change or remove a member.")]
    string UserId,
    [property: Description("First and last name, or null when the account has none.")]
    string? FullName,
    string Email,
    [property: Description("True for the owner, who may do everything everywhere and cannot be changed or removed.")]
    bool IsOwner,
    [property: Description("One per workspace, or one for all. Empty for the owner.")]
    IReadOnlyList<GrantResponse> Grants,
    [property: Description("When they joined; for the owner, when their first workspace was made.")]
    DateTime JoinedAt
);

[Description("An invitation still waiting for an answer.")]
public record TeamInviteResponse(
    Guid Id,
    string FullName,
    string Email,
    IReadOnlyList<GrantResponse> Grants,
    DateTime InvitedAt,
    [property: Description("When the link stops working. Resending gives it a fresh week.")]
    DateTime ExpiresAt
);

[Description("One of the owner's workspaces, for choosing where a grant applies.")]
public record TeamWorkspaceResponse(string Id, string? Name, string? LogoUrl);

[Description("The owner's team: the owner first, then members, then pending invitations, and the workspaces grants can name.")]
public record TeamResponse(
    IReadOnlyList<TeamMemberResponse> Members,
    IReadOnlyList<TeamInviteResponse> Invites,
    IReadOnlyList<TeamWorkspaceResponse> Workspaces
);

[Description("One permission or channel a grant can include.")]
public record OptionResponse(string Key, string Label);

[Description("A role a grant can use: built in (read-only, the same in every business) or one of the owner's own.")]
public record RoleResponse(
    [property: Description("What a grant names. Built-in ids are words such as \"analyst\"; the owner's own are ids.")]
    string Id,
    string Name,
    [property: Description("True for one of the owner's own roles. Built-in roles cannot be changed; duplicate one to make your own.")]
    bool IsCustom,
    IReadOnlyList<string> Permissions,
    [property: Description("How many people hold it in the owner's team. A role held by anyone cannot be deleted.")]
    int Holders
);

[Description("A role of the owner's own: a name and the permissions it gives.")]
public record SaveRoleRequest
{
    [Required(ErrorMessage = "Name the role.")]
    [StringLength(64, MinimumLength = 1, ErrorMessage = "A role name must be 64 characters or fewer.")]
    [Description("Unique among the owner's roles, ignoring case. It may match a built-in role's name.")]
    public string Name { get; init; } = string.Empty;

    [Description("Permission keys from GET /api/team/roles. At least one.")]
    public IReadOnlyList<string> Permissions { get; init; } = [];
}

[Description("The roles (built-in first, then the owner's own), permissions and channels the Team page offers.")]
public record TeamRolesResponse(IReadOnlyList<RoleResponse> Roles, IReadOnlyList<OptionResponse> Permissions, IReadOnlyList<OptionResponse> Channels);

[Description("An invitation to send: who, and what they get in which workspaces.")]
public record CreateInviteRequest
{
    [Required(ErrorMessage = "Enter their full name.")]
    [StringLength(80, MinimumLength = 1, ErrorMessage = "The name must be 80 characters or fewer.")]
    public string FullName { get; init; } = string.Empty;

    [Required(ErrorMessage = "Enter their email address.")]
    [EmailAddress(ErrorMessage = "Enter a valid email address.")]
    [StringLength(256, ErrorMessage = "The email address must be 256 characters or fewer.")]
    public string Email { get; init; } = string.Empty;

    [Required(ErrorMessage = "Give them access to at least one workspace.")]
    [Description("At least one. At most one with a null workspaceId, and each workspace at most once.")]
    public IReadOnlyList<GrantDto> Grants { get; init; } = [];
}

[Description("A member's grants, replacing the ones they hold.")]
public record UpdateMemberRequest
{
    [Required(ErrorMessage = "Give them access to at least one workspace.")]
    [Description("At least one; to take everything away, remove the member instead.")]
    public IReadOnlyList<GrantDto> Grants { get; init; } = [];
}

[Description("What an invite link is for, shown before the person signs in to accept it.")]
public record InvitePreviewResponse(
    [property: Description("The owner's business: the name of their first workspace.")]
    string TeamName,
    string? TeamLogoUrl,
    [property: Description("Who sent it, by name, or by email when they have no name.")]
    string InvitedBy,
    [property: Description("The address it was sent to. Only that account can accept it.")]
    string Email,
    [property: Description("The names of the workspaces it gives access to; \"All workspaces\" for an all-workspaces grant.")]
    IReadOnlyList<string> Workspaces,
    DateTime ExpiresAt,
    [property: Description("pending, accepted, revoked or expired.")]
    string Status,
    [property: Description("True when an account already uses the invited address: sign in to accept. False: set a password to create one.")]
    bool HasAccount,
    [property: Description("The name the owner typed, to start the sign-up form with.")]
    string FullName
);

[Description("A new account for an invited address, made from the invite link. The address is the invite's.")]
public record InviteSignUpRequest
{
    [Required(ErrorMessage = "Enter your first name.")]
    [StringLength(100, MinimumLength = 1, ErrorMessage = "Your first name must be 100 characters or fewer.")]
    public string FirstName { get; init; } = string.Empty;

    [StringLength(100, ErrorMessage = "Your last name must be 100 characters or fewer.")]
    public string? LastName { get; init; }

    [Required(ErrorMessage = "Choose a password.")]
    public string Password { get; init; } = string.Empty;
}
