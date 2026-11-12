using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Parrot.SharedComponents.DesignSystem;
using Parrot.SharedComponents.Layout;
using Parrot.Web.Components.Pages.Dashboard.Dashboard;
using Parrot.Web.Features.Auth.Models;
using Parrot.Web.Features.Auth.State;
using Xunit;

namespace Test.Parrot.Api.Tests.Web.Dashboard;

// Render the actual shortcut fragments without demo data or unrelated dashboard API calls.
public sealed class OverviewToolsTests
{
    [Fact]
    public async Task OwnerShortcutIsToolsAndOpensTheExistingToolsPage()
    {
        await using var view = await ShortcutView.OpenAsync(member: false);
        Assert.Contains(">Tools<", view.Html);
        Assert.Contains("Connect the apps you use.", view.Html);
        Assert.DoesNotContain("Integrations", view.Html);
        Assert.DoesNotContain("Soon", view.Html);
        Assert.All(view.Rows, row => Assert.False(row.Disabled));

        await view.ClickAsync(view.Rows.Last());
        Assert.Equal("http://localhost/dashboard/tools", view.Navigation.Uri);
        Assert.Equal("Tools", NavMap.Match(new Uri(view.Navigation.Uri).AbsolutePath)?.Label);
        Assert.Contains(typeof(global::Parrot.Web.Components.Pages.Dashboard.Tools.Tools).GetCustomAttributes<RouteAttribute>(), route => route.Template == "/dashboard/tools");
    }

    [Theory]
    [InlineData("workspace.manage", true)]
    [InlineData("conversations.view", false)]
    [InlineData("", false)]
    public async Task MemberShortcutUsesTheExistingToolsAccessRule(string permission, bool visible)
    {
        await using var view = await ShortcutView.OpenAsync(member: true, permission);
        Assert.Equal(visible, view.Html.Contains(">Tools<", StringComparison.Ordinal));
        if (!visible) return;
        Assert.Contains("Connect the apps your team and agents use.", view.Html);
        Assert.Equal(2, view.Rows.Count); // Knowledge, then Tools for workspace.manage, in rail order.
        await view.ClickAsync(view.Rows.Last());
        Assert.Equal("http://localhost/dashboard/tools", view.Navigation.Uri);
    }

    private sealed class ShortcutView(ServiceProvider services, HtmlRenderer renderer, TestNavigation navigation, Capture capture, string html) : IAsyncDisposable
    {
        public string Html { get; } = html;
        public TestNavigation Navigation { get; } = navigation;
        public List<RowButton> Rows { get; } = capture.Items.OfType<RowButton>().ToList();
        public Task ClickAsync(RowButton row) => renderer.Dispatcher.InvokeAsync(() => row.OnClick.InvokeAsync());

        public static async Task<ShortcutView> OpenAsync(bool member, string permission = "")
        {
            var navigation = new TestNavigation();
            var auth = new AuthState();
            auth.SetUser(new AuthResponse { Role = member ? "Custom role" : "Owner", Permissions = string.IsNullOrEmpty(permission) ? [] : [permission] });
            var page = new ShortcutPage();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            typeof(DashboardHome).GetProperty("AuthState", flags)!.SetValue(page, auth);
            typeof(DashboardHome).GetProperty("Nav", flags)!.SetValue(page, navigation);
            var fragment = (RenderFragment)typeof(DashboardHome).GetProperty(member ? "MemberShortcuts" : "Shortcuts", flags)!.GetValue(page)!;
            var capture = new Capture();
            var services = new ServiceCollection().AddLogging().AddSingleton<IComponentActivator>(capture).BuildServiceProvider();
            var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
            var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<FragmentHost>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Content"] = fragment })));
            var html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
            return new(services, renderer, navigation, capture, html);
        }

        public async ValueTask DisposeAsync() { await renderer.DisposeAsync(); await services.DisposeAsync(); }
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/dashboard");
        protected override void NavigateToCore(string uri, bool forceLoad) => Uri = ToAbsoluteUri(uri).AbsoluteUri;
    }

    // The actual page's fragments/callbacks, without rerendering its unmounted API-driven shell.
    private sealed class ShortcutPage : DashboardHome, IHandleEvent
    {
        Task IHandleEvent.HandleEventAsync(EventCallbackWorkItem callback, object? arg) => callback.InvokeAsync(arg);
    }

    private sealed class Capture : IComponentActivator
    {
        public List<IComponent> Items { get; } = [];
        public IComponent CreateInstance(Type type) { var item = (IComponent)Activator.CreateInstance(type)!; Items.Add(item); return item; }
    }

    public sealed class FragmentHost : ComponentBase
    {
        [Parameter] public RenderFragment? Content { get; set; }
        protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, Content);
    }
}
