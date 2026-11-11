using Microsoft.AspNetCore.Components;
using Parrot.Web.Features.Auth.State;
using Parrot.Web.Services;

namespace Parrot.Web.Features.Auth.ResetPassword.State;

/// <summary>Ends this browser's saved sign-in after the server accepts a recovery reset.
/// This is not server-side token revocation: other sessions and the security email are backend responsibilities.</summary>
public sealed class PasswordResetSignOut(TokenStorageService storage, AuthState auth, NavigationManager navigation)
{
    public const string CompletePath = "/reset-password/complete";
    public bool ConfirmationPending { get; private set; }
    public void ReachedConfirmation() => ConfirmationPending = false;

    public async Task CompleteAsync()
    {
        // Do not claim sign-out if browser storage could not be cleared. The caller offers a cleanup retry,
        // never another submission of the now-used reset token.
        await storage.ClearAsync();
        // Remote navigation is asynchronous. Tell the route guard the intended public destination before
        // clearing auth, so its notification cannot race this navigation with a redirect to /login.
        ConfirmationPending = true;
        auth.ClearUser();
        navigation.NavigateTo(CompletePath, replace: true);
    }
}
