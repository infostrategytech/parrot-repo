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
    DateTime CreatedAt
);

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
