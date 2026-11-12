using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.JSInterop;
using System.Reflection;
using Parrot.Web.Components;
using Parrot.Web.Features.Auth.Models;
using Parrot.Web.Features.Auth.ResetPassword.State;
using Parrot.Web.Features.Auth.State;
using Parrot.Web.Services;

namespace Test.Parrot.Api.Tests.Web.Settings;

public sealed class PasswordResetSignOutTests
{
    [Fact]
    public async Task CompletionClearsPersistedCredentialsAndAnnouncesPublicDestinationBeforeAuthNotification()
    {
        var js = new Js(); var storage = new TokenStorageService(new ProtectedLocalStorage(js, new EphemeralDataProtectionProvider()));
        var auth = new AuthState(); var navigation = new Navigation();
        var user = new AuthResponse { UserId = "controlled-user", Token = "controlled-session", ExpiresAt = DateTime.UtcNow.AddHours(1) };
        await storage.SaveAsync(user); auth.SetUser(user);
        var notified = false;
        var completion = new PasswordResetSignOut(storage, auth, navigation);
        navigation.BeforeNavigate = () => Assert.Null(js.Saved);
        auth.OnChange += () => { notified = true; Assert.True(completion.ConfirmationPending); Assert.Null(js.Saved); };
        await completion.CompleteAsync();
        Assert.True(notified); Assert.Null(auth.CurrentUser); Assert.Null(await storage.LoadAsync());
        Assert.True(navigation.Replace); Assert.False(navigation.ForceLoad);
        completion.ReachedConfirmation(); Assert.False(completion.ConfirmationPending);
    }

    [Fact]
    public async Task StorageFailureDoesNotPretendToHaveSignedOutOrNavigateAway()
    {
        var js = new Js { FailRemoval = true };
        var storage = new TokenStorageService(new ProtectedLocalStorage(js, new EphemeralDataProtectionProvider()));
        var auth = new AuthState(); auth.SetUser(new AuthResponse { Token = "controlled-session", ExpiresAt = DateTime.UtcNow.AddHours(1) });
        var navigation = new Navigation();
        await Assert.ThrowsAsync<JSException>(() => new PasswordResetSignOut(storage, auth, navigation).CompleteAsync());
        Assert.True(auth.IsAuthenticated); Assert.Null(navigation.Target);
    }

    [Fact]
    public async Task ProtectedRouteGuardKeepsTheResetConfirmationEvenBeforeRemoteNavigationCompletes()
    {
        var js = new Js(); var auth = new AuthState(); var navigation = new Navigation();
        var completion = new PasswordResetSignOut(new TokenStorageService(new ProtectedLocalStorage(js, new EphemeralDataProtectionProvider())), auth, navigation);
        var routes = new Routes(); var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(Routes).GetProperty("Nav", flags)!.SetValue(routes, navigation);
        typeof(Routes).GetProperty("AuthState", flags)!.SetValue(routes, auth);
        typeof(Routes).GetProperty("ResetSignOut", flags)!.SetValue(routes, completion);
        typeof(Routes).GetField("_ready", flags)!.SetValue(routes, true);
        await completion.CompleteAsync();
        // Uri deliberately still points at Settings: RemoteNavigationManager does not update it synchronously.
        Assert.EndsWith("/dashboard/settings/security", navigation.Uri);
        await (Task)typeof(Routes).GetMethod("EnforceSessionAsync", flags)!.Invoke(routes, null)!;
        Assert.Equal(PasswordResetSignOut.CompletePath, navigation.Target);
        navigation.Arrive(PasswordResetSignOut.CompletePath);
        await (Task)typeof(Routes).GetMethod("EnforceSessionAsync", flags)!.Invoke(routes, null)!;
        Assert.False(completion.ConfirmationPending);
        navigation.Arrive("/dashboard/inbox");
        await (Task)typeof(Routes).GetMethod("EnforceSessionAsync", flags)!.Invoke(routes, null)!;
        Assert.Equal("/login", navigation.Target);
    }

    private sealed class Navigation : NavigationManager
    {
        public string? Target; public bool Replace, ForceLoad; public Action? BeforeNavigate;
        public Navigation() => Initialize("http://localhost/", "http://localhost/dashboard/settings/security");
        public void Arrive(string path) => Uri = ToAbsoluteUri(path).AbsoluteUri;
        protected override void NavigateToCore(string uri, bool forceLoad) => throw new NotSupportedException();
        protected override void NavigateToCore(string uri, NavigationOptions options)
        { BeforeNavigate?.Invoke(); Target = uri; Replace = options.ReplaceHistoryEntry; ForceLoad = options.ForceLoad; }
    }
    private sealed class Js : IJSRuntime
    {
        public string? Saved; public bool FailRemoval;
        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args)
        {
            if (id == "localStorage.setItem") Saved = (string?)args![1];
            if (id == "localStorage.removeItem") { if (FailRemoval) throw new JSException("Controlled failure"); Saved = null; }
            return ValueTask.FromResult(id == "localStorage.getItem" && Saved is not null ? (T)(object)Saved : default!);
        }
        public ValueTask<T> InvokeAsync<T>(string id, CancellationToken ct, object?[]? args) => InvokeAsync<T>(id, args);
    }
}
