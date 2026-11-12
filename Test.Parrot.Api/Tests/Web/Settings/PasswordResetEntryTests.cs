using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Parrot.SharedComponents.DesignSystem;
using Parrot.SharedComponents.Services;
using Parrot.Web.Components.Layout.Auth;
using Parrot.Web.Features.Auth.Models;
using Parrot.Web.Features.Auth.ResetPassword.Models;
using Parrot.Web.Features.Auth.ResetPassword.Services;
using Parrot.Web.Features.Auth.ResetPassword.State;
using Parrot.Web.Features.Auth.State;
using Parrot.Web.Services;
using Xunit;
using ResetEntry = Parrot.Web.Components.Pages.Auth.ResetPassword.ResetPassword;

namespace Test.Parrot.Api.Tests.Web.Settings;

public sealed class PasswordResetEntryTests
{
    [Fact]
    public void PublicEmailEntryNeverInheritsTheDefaultProtectedDashboardLayout()
    { Assert.Equal(typeof(PasswordResetEntryLayout), typeof(ResetEntry).GetCustomAttribute<LayoutAttribute>()!.LayoutType); }

    [Fact]
    public async Task AStoredMatchingSessionReturnsTheActualEmailTokenToSettingsAndReplacesTheAddress()
    {
        await using var page = await Page.Open(storedUser: "controlled-user");
        await page.Link("controlled-user", "controlled+/token=="); await page.Call("OnAfterRenderAsync", true);
        Assert.Equal("/dashboard/settings/security", page.Nav.Target); Assert.True(page.Nav.Replace);
        var link = page.Handoff.Take("controlled-user"); Assert.Equal("controlled+/token==", link!.Token);
        Assert.DoesNotContain("token", page.Nav.Target); Assert.Empty(page.Api.Requests);
        Assert.Null(page.Handoff.Take("controlled-user"));
    }

    [Fact]
    public async Task WithoutASessionTheSameLinkRendersTheExistingSignedOutResetForm()
    {
        await using var page = await Page.Open(); await page.Link("controlled-user", "controlled-token");
        await page.Call("OnAfterRenderAsync", true);
        var html = await page.Html(); Assert.Contains("Choose a New Password", html); Assert.Contains("reset-new-password", html);
        Assert.Contains("Save New Password", html); Assert.DoesNotContain("content-modal-backdrop", html);
        Assert.Null(page.Nav.Target); Assert.Null(page.Handoff.Take("controlled-user"));
    }

    [Fact]
    public async Task AnExpiredStoredSessionStillAllowsRecoveryWithoutSigningInFirst()
    {
        await using var page = await Page.Open(storedUser: "controlled-user", expired: true);
        await page.Link("controlled-user", "controlled-token"); await page.Call("OnAfterRenderAsync", true);
        Assert.Null(page.Nav.Target); Assert.Null(page.Auth.CurrentUser); Assert.True(page.Storage.LastLoadExpired);
        Assert.Contains("reset-new-password", await page.Html());
        page.Flow.SetPassword("ValidPass1"); page.Flow.SetConfirmation("ValidPass1"); await page.Call("SaveAsync");
        Assert.Single(page.Api.Requests); Assert.Equal(PasswordResetSignOut.CompletePath, page.Nav.Target);
    }

    [Fact]
    public async Task SignedOutSuccessMovesToCredentialFreeConfirmationNotAutomaticLogin()
    {
        await using var page = await Page.Open(); await page.Link("controlled-user", "controlled-token");
        await page.Call("OnAfterRenderAsync", true);
        page.Flow.SetPassword("ValidPass1"); page.Flow.SetConfirmation("ValidPass1"); await page.Call("SaveAsync");
        Assert.Equal(PasswordResetSignOut.CompletePath, page.Nav.Target); Assert.True(page.Nav.Replace);
        Assert.Null(page.Auth.CurrentUser); Assert.Null(await page.Storage.LoadAsync());
        Assert.DoesNotContain("token", page.Nav.Target); Assert.Equal("", page.Flow.Password);
    }

    [Fact]
    public async Task AUsedOrExpiredTokenOffersANewLinkWithoutASignInDetour()
    {
        await using var page = await Page.Open(); await page.Link("controlled-user", "controlled-token");
        await page.Call("OnAfterRenderAsync", true);
        page.Api.Failure = new ApiException(System.Net.HttpStatusCode.BadRequest, "Invalid token", "InvalidToken", []);
        page.Flow.SetPassword("ValidPass1"); page.Flow.SetConfirmation("ValidPass1"); await page.Call("SaveAsync");
        Assert.True(page.Flow.LinkSpent); Assert.Null(page.Nav.Target);
        Assert.Contains("Send a new link", await page.Html()); Assert.Empty(page.Flow.Password);
        await page.Call("RequestNewLink"); Assert.Equal("/forgot-password", page.Nav.Target);
    }

    [Fact]
    public async Task AnotherSignedInAccountGetsAnExplanationNotASilentCrossAccountReset()
    {
        await using var page = await Page.Open(storedUser: "another-user"); await page.Link("controlled-user", "controlled-token");
        await page.Call("OnAfterRenderAsync", true);
        var html = await page.Html(); Assert.Contains("This link is for another account", html);
        Assert.DoesNotContain("reset-new-password", html); Assert.Null(page.Nav.Target); Assert.Empty(page.Api.Requests);
    }

    [Fact]
    public async Task AnIncompleteLinkNeverOpensASettingsResetOrAnEditablePasswordForm()
    {
        await using var page = await Page.Open(); await page.Link("controlled-user", null); await page.Call("OnAfterRenderAsync", true);
        var html = await page.Html(); Assert.Contains("This reset link is incomplete.", html); Assert.Contains("Send a new link", html);
        Assert.DoesNotContain("reset-new-password", html); Assert.Null(page.Nav.Target); Assert.Empty(page.Api.Requests);
    }

    [Fact]
    public void HandoffCredentialsCannotAppearThroughTheDefaultRecordDiagnosticString()
    { Assert.DoesNotContain("controlled-secret", new PasswordResetLink("controlled-user", "controlled-secret").ToString()); }

    private sealed class Api : IResetPasswordService
    {
        public Exception? Failure;
        public List<ResetPasswordRequest> Requests = [];
        public Task ResetPasswordAsync(ResetPasswordRequest request) { Requests.Add(request); return Failure is null ? Task.CompletedTask : Task.FromException(Failure); }
    }
    private sealed class Navigation : NavigationManager
    {
        public string? Target; public bool Replace;
        public Navigation() => Initialize("http://localhost/", "http://localhost/reset-password");
        protected override void NavigateToCore(string uri, bool forceLoad) => Target = uri;
        protected override void NavigateToCore(string uri, NavigationOptions options) { Target = uri; Replace = options.ReplaceHistoryEntry; }
    }
    private sealed class Js : IJSRuntime
    {
        private readonly Dictionary<string, string?> _storage = [];
        public ValueTask<T> InvokeAsync<T>(string method, object?[]? args)
        {
            if (method == "localStorage.setItem") _storage[(string)args![0]!] = (string?)args[1];
            if (method == "localStorage.removeItem") _storage.Remove((string)args![0]!);
            if (method == "localStorage.getItem" && _storage.TryGetValue((string)args![0]!, out var value)) return ValueTask.FromResult((T)(object)value!);
            return ValueTask.FromResult(default(T)!);
        }
        public ValueTask<T> InvokeAsync<T>(string method, CancellationToken ct, object?[]? args) => InvokeAsync<T>(method, args);
    }
    private sealed class Page : IAsyncDisposable
    {
        public Api Api = new(); public Navigation Nav = new(); public PasswordResetHandoff Handoff = new();
        public AuthState Auth = new(); public TokenStorageService Storage = null!;
        private Capture _capture = new(); private ServiceProvider _services = null!; private HtmlRenderer _renderer = null!;
        private Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent _root;
        private ResetEntry Entry => _capture.Components.OfType<ResetEntry>().Single();
        public PasswordResetFlow Flow => (PasswordResetFlow)typeof(ResetEntry).GetField("_flow", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Entry)!;
        public static async Task<Page> Open(string? storedUser = null, bool expired = false)
        {
            var page = new Page(); var js = new Js(); var auth = page.Auth;
            var storage = new TokenStorageService(new ProtectedLocalStorage(js, new EphemeralDataProtectionProvider()));
            page.Storage = storage;
            if (storedUser is not null) await storage.SaveAsync(new AuthResponse { UserId = storedUser, Token = "controlled-session", ExpiresAt = DateTime.UtcNow.AddHours(expired ? -1 : 1) });
            page._services = new ServiceCollection().AddLogging().AddSingleton<IJSRuntime>(js).AddSingleton(auth).AddSingleton(storage)
                .AddSingleton(page.Handoff).AddSingleton<NavigationManager>(page.Nav).AddSingleton<IResetPasswordService>(page.Api)
                .AddSingleton<PasswordResetSignOut>()
                .AddSingleton<AuroraInterop>().AddSingleton<ToastService>().AddSingleton<IComponentActivator>(page._capture).BuildServiceProvider();
            page._renderer = new(page._services, page._services.GetRequiredService<ILoggerFactory>());
            page._root = await page._renderer.Dispatcher.InvokeAsync(() => page._renderer.RenderComponentAsync<ResetEntry>());
            return page;
        }
        public Task Link(string? userId, string? token) => _renderer.Dispatcher.InvokeAsync(() =>
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(ResetEntry).GetProperty("UserId", flags)!.SetValue(Entry, userId); typeof(ResetEntry).GetProperty("Token", flags)!.SetValue(Entry, token);
            typeof(ResetEntry).GetMethod("OnParametersSet", flags)!.Invoke(Entry, null);
        });
        public Task Call(string method, params object[] arguments) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            if (typeof(ResetEntry).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Entry, arguments) is Task task) await task;
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Entry, null);
        });
        public Task<string> Html() => _renderer.Dispatcher.InvokeAsync(_root.ToHtmlString);
        public async ValueTask DisposeAsync() { await _renderer.DisposeAsync(); await _services.DisposeAsync(); }
    }
    private sealed class Capture : IComponentActivator { public List<IComponent> Components = []; public IComponent CreateInstance(Type type) { var result = (IComponent)Activator.CreateInstance(type)!; Components.Add(result); return result; } }
}
