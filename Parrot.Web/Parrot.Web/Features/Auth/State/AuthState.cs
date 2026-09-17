using Parrot.Web.Features.Auth.Models;

namespace Parrot.Web.Features.Auth.State;

public class AuthState
{
    public AuthResponse? CurrentUser { get; private set; }
    public bool IsAuthenticated => CurrentUser is not null && CurrentUser.ExpiresAt > DateTime.UtcNow;

    public event Action? OnChange;
    public event Action? SessionExpired;

    public void ReportUnauthorized(string? token)
    {
        // A late response for an older token must not sign out a newly authenticated user.
        if (!string.IsNullOrEmpty(token) && CurrentUser?.Token == token)
            SessionExpired?.Invoke();
    }

    public void SetUser(AuthResponse user)
    {
        CurrentUser = user;
        NotifyStateChanged();
    }

    public void ClearUser()
    {
        CurrentUser = null;
        NotifyStateChanged();
    }

    private void NotifyStateChanged() => OnChange?.Invoke();
}
