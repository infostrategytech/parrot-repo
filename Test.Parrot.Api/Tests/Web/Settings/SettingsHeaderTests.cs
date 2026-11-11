using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Parrot.Web.Features.Auth.Models;
using Parrot.Web.Features.Auth.State;
using Parrot.Web.Features.Settings;
using Parrot.Web.Services;
using Parrot.SharedComponents.Services;
using Microsoft.JSInterop;

namespace Test.Parrot.Api.Tests.Web.Settings;

public sealed class SettingsHeaderTests
{
    [Fact]
    public async Task PageHeaderUsesCustomersTitleAndSupportingTextStyles()
    {
        // Activity with no log yet (UnavailableActivityLog): no records load, so this verifies the real shared Settings
        // header, not demo records or a duplicated header fixture.
        var auth = new AuthState();
        auth.SetUser(new AuthResponse { UserId = "controlled-owner", Role = "Owner" });
        await using var services = new ServiceCollection().AddLogging().AddSingleton(auth)
            .AddSingleton<IActivityLog, UnavailableActivityLog>().AddSingleton<IJSRuntime>(new NoJs()).AddSingleton<AuroraInterop>()
            .AddSingleton<UserClock>().AddSingleton<ToastService>().AddSingleton<NavigationManager>(new TestNavigation()).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<global::Parrot.Web.Components.Pages.Dashboard.Settings.Settings>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { ["Tab"] = "activity" }));
            return root.ToHtmlString();
        });
        Assert.Matches("<h1[^>]*class=\"ds-heading-h1-bold\"[^>]*>Activity log</h1>", html);
        Assert.Contains("class=\"ds-body-small-medium ds-prose\"", html);
        Assert.Contains("Who changed what, and when.", html);
        Assert.DoesNotContain("ds-heading-h2-semibold", html);
    }

    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new InvalidOperationException("Static rendering has no browser.");
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/dashboard/settings/activity");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }
}
