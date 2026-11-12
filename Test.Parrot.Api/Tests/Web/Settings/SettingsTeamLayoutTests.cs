using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Parrot.SharedComponents.Services;
using Parrot.Web.Components.Layout.Settings;
using Parrot.Web.Components.Pages.Dashboard.Settings;
using Parrot.Web.Features.Auth.Models;
using Parrot.Web.Features.Auth.State;
using Parrot.Web.Features.Notifications;
using Parrot.Web.Features.Settings;
using Parrot.Web.Services;

namespace Test.Parrot.Api.Tests.Web.Settings;

// The table's wider measure belongs to Team only, including when navigating without reloading.
public sealed class SettingsTeamLayoutTests
{
    [Theory]
    [InlineData("/dashboard/settings/team")]
    [InlineData("/dashboard/settings/TEAM/")]
    [InlineData("/dashboard/settings/activity")] // A records page too (designer, 9 November 2026).
    public async Task TeamUsesTheWiderColumn(string path)
    {
        await using var page = await Page.Open(path);
        var html = await page.Html();
        Assert.Contains("settings-shell--wide", html);
        Assert.Contains("Controlled section content", html);
        Assert.Contains("aria-current=\"page\"", html);
    }

    [Theory]
    [InlineData("/dashboard/settings")]
    [InlineData("/dashboard/settings/profile")]
    [InlineData("/dashboard/settings/appearance")]
    [InlineData("/dashboard/settings/notifications")]
    [InlineData("/dashboard/settings/security")]
    [InlineData("/dashboard/settings/business")]
    [InlineData("/dashboard/settings/billing")]
    [InlineData("/dashboard/settings/unknown")]
    public async Task OtherSettingsKeepTheirExistingMeasure(string path)
    {
        await using var page = await Page.Open(path);
        Assert.DoesNotContain("settings-shell--wide", await page.Html());
    }

    [Fact]
    public async Task InaccessibleTeamRouteDoesNotWidenTheFallbackProfile()
    {
        await using var page = await Page.Open("/dashboard/settings/team", owner: false);
        Assert.DoesNotContain("settings-shell--wide", await page.Html());
    }

    [Fact]
    public async Task MovingBetweenSettingsUpdatesTheMeasureWithoutAReload()
    {
        await using var page = await Page.Open("/dashboard/settings/security");
        await page.Move("/dashboard/settings/team");
        Assert.Contains("settings-shell--wide", await page.Html());
        await page.Move("/dashboard/settings/profile");
        Assert.DoesNotContain("settings-shell--wide", await page.Html());
    }

    private sealed class Page : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly HtmlRenderer _renderer;
        private readonly Nav _nav;
        private Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent _root;

        private Page(string path, bool owner)
        {
            _nav = new(path);
            var auth = new AuthState();
            auth.SetUser(new AuthResponse { UserId = "controlled-person", Role = owner ? "Owner" : "Analyst" });
            _services = new ServiceCollection().AddLogging().AddSingleton(auth)
                .AddSingleton<NavigationManager>(_nav).AddSingleton<SettingsReturn>()
                .AddSingleton<ActivityState>().AddSingleton<ToastService>().AddSingleton<AuroraInterop>().AddSingleton<UserClock>()
                .AddSingleton<INotificationFeed, UnavailableNotificationFeed>()
                .AddSingleton<INotificationSettingsStore, UnavailableNotificationSettings>()
                .AddSingleton<NotificationPopups>().AddSingleton<IJSRuntime>(new NoJs())
                .BuildServiceProvider();
            _renderer = new(_services, _services.GetRequiredService<ILoggerFactory>());
        }

        public static async Task<Page> Open(string path, bool owner = true)
        {
            var page = new Page(path, owner);
            page._root = await page._renderer.Dispatcher.InvokeAsync(() =>
                page._renderer.RenderComponentAsync<SettingsLayout>(ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(LayoutComponentBase.Body)] = (RenderFragment)(builder => builder.AddContent(0, "Controlled section content"))
                })));
            return page;
        }

        public Task<string> Html() => _renderer.Dispatcher.InvokeAsync(_root.ToHtmlString);
        public Task Move(string path) => _renderer.Dispatcher.InvokeAsync(() => _nav.NavigateTo(path));
        public async ValueTask DisposeAsync() { await _renderer.DisposeAsync(); await _services.DisposeAsync(); }
    }

    private sealed class Nav : NavigationManager
    {
        public Nav(string path) => Initialize("http://localhost/", "http://localhost" + path);
        protected override void NavigateToCore(string uri, bool forceLoad) { Uri = ToAbsoluteUri(uri).AbsoluteUri; NotifyLocationChanged(false); }
    }

    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken ct, object?[]? args) => InvokeAsync<T>(identifier, args);
    }
}

