using Parrot.Web.Features.Auth.Models;
using Parrot.Web.Features.Auth.State;
using Parrot.Web.Services;

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
/// Keeps the business name on the signed-in session, so the dashboard and the
/// workspace switcher show it straight away.
///
/// Backend follow-up: there is no endpoint to save a business name, phone
/// number or logo yet (sign-up still asks for the name instead). When one
/// exists, POST all three here before updating the session; the card already
/// handles a failure. Until then the phone number and logo are not stored
/// anywhere and the name lives only in this browser's session.
/// </summary>
public sealed class SessionWorkspaceSetupService(AuthState authState, TokenStorageService tokenStorage) : IWorkspaceSetupService
{
    public async Task SaveAsync(string businessName, string? phoneNumber, WorkspaceLogo? logo)
    {
        AuthResponse current = authState.CurrentUser
            ?? throw new InvalidOperationException("The welcome card only opens for a signed-in person.");

        AuthResponse updated = new()
        {
            Token = current.Token,
            ExpiresAt = current.ExpiresAt,
            UserId = current.UserId,
            Email = current.Email,
            BusinessName = businessName,
        };

        await tokenStorage.SaveAsync(updated);
        authState.SetUser(updated);
    }
}
