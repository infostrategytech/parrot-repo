using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Parrot.SharedComponents.Services;
using Parrot.Web.Components.Pages.Auth;
using Parrot.Web.Components.Pages.Dashboard.Settings;
using Parrot.Web.Features.Auth.Models;
using Parrot.Web.Features.Auth.State;
using Parrot.Web.Features.Profile;
using Parrot.Web.Services;
using Xunit;

namespace Test.Parrot.Api.Tests.Web.Settings;

// Render the real components against controlled service contracts, never local demo records. The activator
// exposes component instances so their event handlers can be exercised without adding a browser dependency.
public sealed class ProfileRecoveryTests
{
    [Fact]
    public async Task FailedProfileLoadCannotExposeOrSaveABlankFormAndCanRetry()
    {
        await using var page = await Page.OpenAsync(failProfile: true);
        var html = await page.Html();
        Assert.Contains("before making changes", html);
        Assert.Contains(">Retry<", html);
        Assert.DoesNotContain("given-name", html);
        await page.Set("_firstName", "A change from a stale event");
        await page.Call(page.Profile, "SaveAsync");
        Assert.Empty(page.Api.Writes);
        page.Api.FailRead = false;
        await page.Call(page.Profile, "LoadProfileAsync");
        Assert.Contains("Ada", await page.Html());
        Assert.Contains("given-name", await page.Html());
    }

    [Fact]
    public async Task OptionalPhotoFailureDoesNotBlockTheProfileAndCanRetry()
    {
        await using var page = await Page.OpenAsync(failPhoto: true);
        var html = await page.Html();
        Assert.Contains("given-name", html);
        Assert.Contains("Retry photo", html);
        page.Photo.FailRead = false;
        await page.Call(page.Profile, "LoadPhotoAsync");
        Assert.DoesNotContain("Retry photo", await page.Html());
    }

    [Fact]
    public async Task SavingOneChangedNameLeavesOtherFieldsUntouched()
    {
        await using var page = await Page.OpenAsync();
        await page.Set("_firstName", "  Adaora  ");
        await page.Call(page.Profile, "SaveAsync");
        var update = Assert.Single(page.Api.Writes);
        Assert.Equal("Adaora", update.FirstName);
        Assert.Null(update.LastName);
        Assert.Null(update.CountryCode);
        Assert.Null(update.Number);
        Assert.Null(update.BusinessName);
        Assert.Null(update.LogoUrl);
        Assert.False(page.Value<bool>(page.Profile, "Changed"));
    }

    [Fact]
    public async Task IntentionallyClearingANameIsDifferentFromLeavingItUnchanged()
    {
        await using var page = await Page.OpenAsync();
        await page.Set("_lastName", " ");
        await page.Call(page.Profile, "SaveAsync");
        Assert.Equal("", Assert.Single(page.Api.Writes).LastName);
        Assert.Null(page.Api.Writes[0].FirstName);
    }

    [Fact]
    public async Task AnUnlistedPhoneSurvivesANameOnlySave()
    {
        await using var page = await Page.OpenAsync(phone: "+49 30 12345678");
        Assert.Contains("+49 30 12345678", WebUtility.HtmlDecode(await page.Html()));
        Assert.Contains("Your saved number is kept as-is", await page.Html());
        await page.Set("_firstName", "Adaora");
        await page.Call(page.Profile, "SaveAsync");
        Assert.Null(Assert.Single(page.Api.Writes).Number);
        Assert.Equal("+49 30 12345678", page.Api.Current.PhoneNumber);
    }

    [Fact]
    public async Task SwitchingAnUnlistedCountryRequiresANewNumberAndCanRestoreTheOriginal()
    {
        await using var page = await Page.OpenAsync(phone: "+49 30 12345678");
        await page.Call(page.Profile, "ChooseCountry", PhoneCountries.Home);
        await page.Call(page.Profile, "SaveAsync");
        Assert.Empty(page.Api.Writes);
        await page.Call(page.Profile, "KeepSavedPhone");
        Assert.False(page.Value<bool>(page.Profile, "Changed"));
        Assert.Contains("+49 30 12345678", WebUtility.HtmlDecode(await page.Html()));
    }

    [Fact]
    public async Task UnsavedNamesPromptBeforeLeavingAndKeepEditingPreservesThem()
    {
        await using var page = await Page.OpenAsync();
        await page.Set("_firstName", "Adaora");
        var location = new LocationChangingContext { TargetLocation = "http://localhost/dashboard/inbox" };
        await page.Call(page.Profile, "CheckNavigationAsync", location);
        Assert.EndsWith("/settings/profile", page.Navigation.Uri);
        Assert.True(page.Value<bool>(page.Profile, "_confirmLeave"));
        await page.Call(page.Profile, "KeepEditing");
        Assert.False(page.Value<bool>(page.Profile, "_confirmLeave"));
        Assert.True(page.Value<bool>(page.Profile, "Changed"));
        await page.Call(page.Profile, "CheckNavigationAsync", location);
        await page.Call(page.Profile, "Leave");
        Assert.Equal(location.TargetLocation, page.Navigation.Uri);
    }

    [Fact]
    public async Task PendingEmailLoadFailureCannotStartAnotherChangeAndCanRetry()
    {
        await using var page = await Page.OpenAsync(failPending: true);
        Assert.Contains("before starting a new one", await page.Html());
        await page.Call(page.Email, "Open");
        Assert.False(page.Value<bool>(page.Email, "_open"));
        page.EmailApi.FailPending = false;
        await page.Call(page.Email, "LoadPendingAsync");
        await page.Call(page.Email, "Open");
        Assert.True(page.Value<bool>(page.Email, "_open"));
    }

    [Fact]
    public async Task EmailConfirmationNetworkFailureKeepsTheChangeAndCanRetry()
    {
        await using var page = await Page.OpenAsync();
        await page.Call(page.Email, "EnterCode");
        page.EmailApi.FailConfirm = true;
        var check = await page.Call<CodeEntry.Outcome>(page.Email, "ConfirmAsync", "123456", CancellationToken.None);
        Assert.NotNull(check.Error);
        Assert.False(check.Locked);
        Assert.True(page.Value<bool>(page.Email, "_open"));
        Assert.Equal("new@example.com", page.Value<string>(page.Email, "_pending"));
        page.EmailApi.FailConfirm = false;
        Assert.Null((await page.Call<CodeEntry.Outcome>(page.Email, "ConfirmAsync", "123456", CancellationToken.None)).Error);
        Assert.False(page.Value<bool>(page.Email, "_open"));
        Assert.Equal("new@example.com", page.Value<string>(page.Profile, "_email"));
        Assert.Equal("Email changed", page.ToastTitle);
    }

    [Fact]
    public async Task WrongAndExpiredCodesKeepTheirExistingMeaning()
    {
        await using var page = await Page.OpenAsync();
        page.EmailApi.Check = new("That code isn't right.");
        Assert.False((await page.Call<CodeEntry.Outcome>(page.Email, "ConfirmAsync", "bad", CancellationToken.None)).Locked);
        page.EmailApi.Check = new("That code expired.", Locked: true);
        Assert.True((await page.Call<CodeEntry.Outcome>(page.Email, "ConfirmAsync", "expired", CancellationToken.None)).Locked);
        Assert.Equal("new@example.com", page.Value<string>(page.Email, "_pending"));
    }

    [Fact]
    public async Task CancellingAnEmailChangeShowsFailureAndCanRetry()
    {
        await using var page = await Page.OpenAsync();
        page.EmailApi.FailCancel = true;
        await page.Call(page.Email, "CancelAsync");
        Assert.Contains("couldn&#x27;t cancel", await page.Html());
        Assert.Equal("new@example.com", page.Value<string>(page.Email, "_pending"));
        page.EmailApi.FailCancel = false;
        await page.Call(page.Email, "CancelAsync");
        Assert.Null(page.Value<string?>(page.Email, "_pending"));
        Assert.DoesNotContain("couldn&#x27;t cancel", await page.Html());
    }

    [Fact]
    public async Task EmailResendTimeoutIsRecoverable()
    {
        await using var page = await Page.OpenAsync();
        page.EmailApi.FailResend = true;
        Assert.NotNull(await page.Call<string?>(page.Email, "ResendAsync", CancellationToken.None));
        page.EmailApi.FailResend = false;
        Assert.Null(await page.Call<string?>(page.Email, "ResendAsync", CancellationToken.None));
        Assert.False(page.Value<bool>(page.Email, "Busy"));
    }

    [Fact]
    public async Task StartingAChangeKeepsPasswordAndEmailErrorsOnTheirOwnFields()
    {
        await using var page = await Page.OpenAsync();
        await page.Call(page.Email, "ChangeDestination");
        await page.Set(page.Email, "_password", "Controlled-password1"); // The form asks for it before sending.
        page.EmailApi.StartProblem = new("That password isn't right.", Password: true);
        await page.Call(page.Email, "StartAsync");
        Assert.Equal("That password isn't right.", page.Value<string>(page.Email, "_passwordError"));
        Assert.Null(page.Value<string?>(page.Email, "_emailError"));
        page.EmailApi.StartProblem = new("That address is already used.");
        await page.Call(page.Email, "StartAsync");
        Assert.Null(page.Value<string?>(page.Email, "_passwordError"));
        Assert.Equal("That address is already used.", page.Value<string>(page.Email, "_emailError"));
        page.EmailApi.StartProblem = null;
        await page.Call(page.Email, "StartAsync");
        Assert.Null(page.Value<string?>(page.Email, "_password"));
        Assert.True(page.Value<bool>(page.Email, "_justSent"));
    }

    [Fact]
    public async Task EmailStartTimeoutLeavesTheDetailsAvailableForRetry()
    {
        await using var page = await Page.OpenAsync();
        await page.Call(page.Email, "ChangeDestination");
        await page.Set(page.Email, "_password", "Controlled-password1");
        page.EmailApi.FailStart = true;
        await page.Call(page.Email, "StartAsync");
        Assert.Contains("Check your connection", page.Value<string>(page.Email, "_emailError"));
        Assert.False(page.Value<bool>(page.Email, "Busy"));
        page.EmailApi.FailStart = false;
        await page.Call(page.Email, "StartAsync");
        Assert.Null(page.Value<string?>(page.Email, "_emailError"));
        Assert.True(page.Value<bool>(page.Email, "_justSent"));
    }

    [Fact]
    public async Task RealUnavailableCapabilitiesStayUnavailable()
    {
        await using var page = await Page.OpenAsync(unavailable: true);
        Assert.Contains("Changing it is coming soon", await page.Html());
        Assert.DoesNotContain("change-email-password", await page.Html());
        Assert.DoesNotContain("Add a photo", await page.Html());
    }

    private sealed class Page : IAsyncDisposable
    {
        public ProfileHttp Api { get; } = new();
        public PhotoApi Photo { get; } = new();
        public EmailApi EmailApi { get; } = new();
        public TestNavigation Navigation { get; } = new();
        private readonly CapturingActivator _components = new();
        private ServiceProvider _services = null!;
        private HtmlRenderer _renderer = null!;
        private Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent _root;
        public ProfileTab Profile => (ProfileTab)_components.Instances.Last(component => component is ProfileTab);
        public EmailChangeRow Email => (EmailChangeRow)_components.Instances.Last(component => component is EmailChangeRow);
        public string? ToastTitle => _services.GetRequiredService<ToastService>().Current?.Title;

        public static async Task<Page> OpenAsync(bool failProfile = false, bool failPhoto = false, bool failPending = false, string? phone = null, bool unavailable = false)
        {
            var page = new Page();
            page.Api.FailRead = failProfile; page.Photo.FailRead = failPhoto; page.EmailApi.FailPending = failPending;
            if (phone is not null) page.Api.Current = page.Api.Current with { PhoneNumber = phone };
            var auth = new AuthState();
            auth.SetUser(new AuthResponse { UserId = "controlled-user", Token = "controlled-token", Email = page.Api.Current.Email });
            var js = new NoJs();
            page._services = new ServiceCollection().AddLogging().AddSingleton(auth).AddSingleton<IJSRuntime>(js)
                .AddSingleton(new ProtectedLocalStorage(js, new EphemeralDataProtectionProvider()))
                .AddSingleton<TokenStorageService>().AddSingleton<AuroraInterop>().AddSingleton<ToastService>()
                .AddSingleton<NavigationManager>(page.Navigation)
                .AddSingleton<IComponentActivator>(page._components)
                .AddSingleton<IProfilePhoto>(unavailable ? new UnavailableProfilePhoto() : page.Photo)
                .AddSingleton<IEmailChange>(unavailable ? new UnavailableEmailChange() : page.EmailApi)
                .AddSingleton(new HttpClient(page.Api) { BaseAddress = new Uri("http://localhost/") })
                .AddSingleton<ProfileClient>()
                // Profile shows the sign-in preference (the backend team's), which reads the workspaces.
                .AddSingleton<global::Parrot.Web.Features.Dashboard.Workspace.WorkspaceClient>().BuildServiceProvider();
            page._renderer = new HtmlRenderer(page._services, page._services.GetRequiredService<ILoggerFactory>());
            page._root = await page._renderer.Dispatcher.InvokeAsync(() => page._renderer.RenderComponentAsync<ProfileTab>());
            return page;
        }

        public Task<string> Html() => _renderer.Dispatcher.InvokeAsync(_root.ToHtmlString);
        public T Value<T>(object component, string name) => (T)(component.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(component)
            ?? component.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(component))!;
        public Task Set(object component, string name, object? value) => _renderer.Dispatcher.InvokeAsync(() => component.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, value));
        public Task Set(string name, object? value) => _renderer.Dispatcher.InvokeAsync(() => typeof(ProfileTab).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Profile, value));
        public Task Call(object component, string name, params object[] args) => Call<object?>(component, name, args);
        public Task<T> Call<T>(object component, string name, params object[] args) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = component.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(component, args);
            if (result is Task task) { await task; result = task.GetType().IsGenericType ? task.GetType().GetProperty("Result")?.GetValue(task) : null; }
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(component, null);
            return result is T value ? value : default!;
        });
        public async ValueTask DisposeAsync() { await _renderer.DisposeAsync(); await _services.DisposeAsync(); }
    }

    private sealed class CapturingActivator : IComponentActivator
    {
        public List<IComponent> Instances { get; } = [];
        public IComponent CreateInstance(Type componentType)
        { var component = (IComponent)Activator.CreateInstance(componentType)!; Instances.Add(component); return component; }
    }
    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken ct, object?[]? args) => InvokeAsync<T>(identifier, args);
    }
    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/dashboard/settings/profile");
        protected override void NavigateToCore(string uri, bool forceLoad) { Uri = ToAbsoluteUri(uri).AbsoluteUri; NotifyLocationChanged(false); }
    }
    private sealed class ProfileHttp : HttpMessageHandler
    {
        public bool FailRead;
        public UserProfile Current = new("controlled-user", "old@example.com", "Ada", "Okafor", "Example business", null, "+2348035551234");
        public List<UserProfileUpdate> Writes { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get && FailRead) throw new HttpRequestException("Controlled network failure");
            if (request.Method == HttpMethod.Put)
            {
                var update = (await request.Content!.ReadFromJsonAsync<UserProfileUpdate>(ct))!;
                Writes.Add(update);
                Current = Current with { FirstName = update.FirstName ?? Current.FirstName, LastName = update.LastName ?? Current.LastName };
            }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(Current) };
        }
    }
    private sealed class PhotoApi : IProfilePhoto
    {
        public bool FailRead;
        public bool Available => true;
        public string? Url => null;
        public event Action? Changed { add { } remove { } }
        public Task<string?> ReadAsync(CancellationToken ct = default) => FailRead ? throw new HttpRequestException("Controlled photo failure") : Task.FromResult<string?>(null);
        public Task<string> SaveAsync(string type, byte[] bytes, CancellationToken ct = default) => Task.FromResult("controlled-photo");
        public Task RemoveAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
    private sealed class EmailApi : IEmailChange
    {
        public bool FailPending, FailConfirm, FailCancel, FailResend, FailStart;
        public CodeCheck Check = CodeCheck.Ok;
        public EmailChangeProblem? StartProblem;
        public bool Available => true;
        public Task<string?> PendingAsync(CancellationToken ct = default) => FailPending ? throw new HttpRequestException("Controlled pending failure") : Task.FromResult<string?>("new@example.com");
        public Task<EmailChangeProblem?> StartAsync(string email, string password, CancellationToken ct = default) => FailStart ? throw new TaskCanceledException("Controlled timeout") : Task.FromResult(StartProblem);
        public Task<string?> ResendAsync(CancellationToken ct = default) => FailResend ? throw new TaskCanceledException("Controlled timeout") : Task.FromResult<string?>(null);
        public Task<CodeCheck> ConfirmAsync(string code, CancellationToken ct = default) => FailConfirm ? throw new HttpRequestException("Controlled confirm failure") : Task.FromResult(Check);
        public Task CancelAsync(CancellationToken ct = default) => FailCancel ? throw new HttpRequestException("Controlled cancel failure") : Task.CompletedTask;
    }
}
