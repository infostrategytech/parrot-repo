using Parrot.Web.Features.Profile;

namespace Parrot.Web.Features.Dashboard.Workspace;

/// <summary>
/// Sets up a workspace from what the workspace card collects: the business
/// name (required) and a logo (optional).
/// </summary>
public interface IWorkspaceSetupService
{
    /// <summary>Names the workspace the session is already in: a new account's first one.</summary>
    Task SaveAsync(string businessName, WorkspaceLogo? logo);

    /// <summary>Makes another workspace and switches the session to it.</summary>
    Task CreateAndSwitchAsync(string businessName, WorkspaceLogo? logo);
}

/// <summary>A logo the person chose, already checked for type and size.</summary>
public sealed record WorkspaceLogo(string FileName, string ContentType, byte[] Bytes)
{
    // Encoded once. As a computed property it re-encoded up to 2 MB on every
    // render of the card, which is every keystroke in its fields.
    public string DataUrl { get; } = $"data:{ContentType};base64,{Convert.ToBase64String(Bytes)}";
}

/// <summary>
/// The logo is uploaded first because a workspace stores only its URL;
/// <see cref="WorkspaceClient"/> then brings the session up to date so the
/// dashboard and the workspace switcher show it.
/// </summary>
public sealed class WorkspaceSetupService(ProfileClient profile, WorkspaceClient workspaces) : IWorkspaceSetupService
{
    // A workspace made but not yet switched to, because the switch failed.
    // Trying again with the same name switches to it instead of making a twin.
    private (string Name, string Id)? _unswitched;

    public async Task SaveAsync(string businessName, WorkspaceLogo? logo) =>
        await workspaces.UpdateCurrentAsync(businessName, await UploadAsync(logo));

    public async Task CreateAndSwitchAsync(string businessName, WorkspaceLogo? logo)
    {
        string id = _unswitched is { } made && made.Name == businessName
            ? made.Id
            : (await workspaces.CreateAsync(businessName, await UploadAsync(logo))).Id;

        _unswitched = (businessName, id);
        await workspaces.SwitchAsync(id);
        _unswitched = null;
    }

    private async Task<string?> UploadAsync(WorkspaceLogo? logo) =>
        logo is null ? null : await profile.UploadImageAsync(logo.FileName, logo.ContentType, logo.Bytes);
}
