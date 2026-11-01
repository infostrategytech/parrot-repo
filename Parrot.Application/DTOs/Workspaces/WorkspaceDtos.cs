using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Parrot.Application.Auth;

namespace Parrot.Application.DTOs.Workspaces;

/// <summary>One workspace as the switcher and the settings page draw it.</summary>
[Description("A workspace: one business the signed-in tenant runs.")]
public record WorkspaceResponse(
    [property: Description("The workspace's identifier. Switch to it with POST /api/workspaces/{id}/switch.")]
    string Id,
    [property: Description("The business name, or null until setup has saved one.")]
    string? Name,
    [property: Description("The logo URL, or null when none has been set.")]
    string? LogoUrl,
    [property: Description("True for the workspace the current session is working in.")]
    bool IsCurrent,
    [property: Description("When it was created.")]
    DateTime CreatedAt,
    [property: Description("\"Owner\" for a workspace the tenant made; otherwise the role they were given in it, e.g. \"Conversation Manager\".")]
    string Role = "Owner",
    [property: Description("The business the workspace belongs to: its owner's user id. The switcher groups by it.")]
    string? OwnerId = null,
    [property: Description("How the owner's business reads in the switcher: the name of their first workspace. Null for the person's own.")]
    string? TeamName = null,
    [property: Description("True for the workspace the person chose to always open on sign-in. None is true when they resume where they left off.")]
    bool IsDefault = false
);

/// <summary>"When I sign in": a workspace to always open, or null to resume where the person left off.</summary>
[Description("Where sign-in lands. A workspace id to always open it, or null to resume the last workspace.")]
public record SetDefaultWorkspaceRequest
{
    [Description("A workspace the person owns or was given access to, or null to resume where they left off.")]
    public string? WorkspaceId { get; init; }
}

/// <summary>What creating a workspace takes: a name, and optionally a logo.</summary>
[Description("A new workspace. The name is required; the logo is optional.")]
public record CreateWorkspaceRequest
{
    [Description("The business name, trimmed to between 1 and 256 characters.")]
    [Required(ErrorMessage = AuthMessages.BusinessNameTooLong)]
    [StringLength(256, MinimumLength = 1, ErrorMessage = AuthMessages.BusinessNameTooLong)]
    public string Name { get; init; } = string.Empty;

    [Description("A URL from POST /api/user/image, not the file itself. Leave it out for no logo.")]
    [StringLength(2048, ErrorMessage = "Logo URL must be 2048 characters or fewer")]
    public string? LogoUrl { get; init; }
}

/// <summary>A partial update: a null field is left as it is.</summary>
[Description("A partial update: leave a field out (or null) to leave it as it is. An empty logoUrl removes the logo.")]
public record UpdateWorkspaceRequest
{
    [Description("Trimmed to between 1 and 256 characters.")]
    [StringLength(256, MinimumLength = 1, ErrorMessage = AuthMessages.BusinessNameTooLong)]
    public string? Name { get; init; }

    [Description("A URL from POST /api/user/image. An empty string removes the logo.")]
    [StringLength(2048, ErrorMessage = "Logo URL must be 2048 characters or fewer")]
    public string? LogoUrl { get; init; }
}

/// <summary>What the session may do in its workspace right now, read from the grant rather than the token.</summary>
[Description("What the session may do in its workspace right now. Unlike the token's copy, this reflects any change made since it was issued.")]
public record SessionAccessResponse(
    string WorkspaceId,
    [property: Description("\"Owner\", or the name of the role held here, built-in or the owner's own.")]
    string Role,
    bool IsCustomRole,
    [property: Description("Permission keys, e.g. conversations.reply. An owner has every one, team.manage and workspace.manage among them.")]
    IReadOnlyList<string> Permissions,
    [property: Description("Channel keys the session reaches; empty is every channel.")]
    IReadOnlyList<string> Channels,
    [property: Description("The catalog scopes those permissions add up to, as the catalog service checks them.")]
    string CatalogScopes
);
