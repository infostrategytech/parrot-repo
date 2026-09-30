using Parrot.Web.Features.Profile;

namespace Parrot.Web.Features.Dashboard.Workspace;

/// <summary>
/// Saves what the welcome card collects: the business name (required), and
/// the business phone number and logo (both optional).
/// </summary>
public interface IWorkspaceSetupService
{
    /// <param name="phoneNumber">In E.164, e.g. +2348012345678, or null.</param>
    Task SaveAsync(string businessName, string? phoneNumber, WorkspaceLogo? logo);
}

/// <summary>A logo the person chose, already checked for type and size.</summary>
public sealed record WorkspaceLogo(string FileName, string ContentType, byte[] Bytes)
{
    // Encoded once. As a computed property it re-encoded up to 2 MB on every
    // render of the card, which is every keystroke in its fields.
    public string DataUrl { get; } = $"data:{ContentType};base64,{Convert.ToBase64String(Bytes)}";
}

/// <summary>
/// Saves the card to the account. The logo is uploaded first because the
/// profile stores only its URL; <see cref="ProfileClient"/> then brings the
/// session up to date so the dashboard and the workspace switcher show it.
/// </summary>
public sealed class WorkspaceSetupService(ProfileClient profile) : IWorkspaceSetupService
{
    // The card only offers Nigerian numbers for now.
    private const string NigeriaDialCode = "234";

    public async Task SaveAsync(string businessName, string? phoneNumber, WorkspaceLogo? logo)
    {
        string? logoUrl = logo is null
            ? null
            : await profile.UploadImageAsync(logo.FileName, logo.ContentType, logo.Bytes);

        await profile.UpdateAsync(new UserProfileUpdate
        {
            BusinessName = businessName,
            LogoUrl = logoUrl,
            CountryCode = phoneNumber is null ? null : NigeriaDialCode,
            Number = phoneNumber,
        });
    }
}
