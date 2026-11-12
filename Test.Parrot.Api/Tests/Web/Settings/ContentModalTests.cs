using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Parrot.SharedComponents.DesignSystem;
using Parrot.SharedComponents.Services;

namespace Test.Parrot.Api.Tests.Web.Settings;

// The shared component, driven through controlled contracts; no demo dataset or real API.
public sealed class ContentModalTests
{
    [Fact]
    public async Task ClosedModalDoesNotRenderFocusableContent()
    {
        await using var page = await Harness.Open<ContentModal>(new() { ["Open"] = false, ["Title"] = "Edit details" });
        Assert.DoesNotContain("role=\"dialog\"", await page.Html());
    }
    [Fact]
    public async Task ContentHasAnAccessibleTitleButNoAssumedConfirmationFooter()
    {
        await using var page = await Harness.Open<ContentModal>(new() { ["Open"] = true, ["Title"] = "Edit details", ["Subtitle"] = "Change what you need." });
        var html = await page.Html();
        Assert.Contains("role=\"dialog\"", html); Assert.Contains("aria-modal=\"true\"", html);
        Assert.Contains("aria-labelledby=\"content-modal-title-", html);
        Assert.Contains("Change what you need.", html); Assert.DoesNotContain("<footer", html);
        Assert.DoesNotContain(">Confirm<", html); Assert.DoesNotContain(">Cancel<", html);
    }
    [Fact]
    public async Task NativeFormAndFooterShareAnIdAndOneSubmitPath()
    {
        var submits = 0;
        RenderFragment<string> footer = id => builder => { builder.OpenComponent<Button>(0); builder.AddAttribute(1, "Type", ButtonType.Submit); builder.AddAttribute(2, "form", id); builder.AddAttribute(3, "ChildContent", (RenderFragment)(b => b.AddContent(0, "Save"))); builder.CloseComponent(); };
        await using var page = await Harness.Open<ContentModal>(new() { ["Open"] = true, ["Title"] = "Edit", ["OnSubmit"] = EventCallback.Factory.Create(new object(), () => submits++), ["Footer"] = footer });
        var html = await page.Html();
        var formId = System.Text.RegularExpressions.Regex.Match(html, "<form id=\"([^\"]+)").Groups[1].Value;
        Assert.NotEmpty(formId); Assert.Contains($"form=\"{formId}\"", html);
        await page.Call(page.Root<ContentModal>(), "SubmitAsync"); Assert.Equal(1, submits);
    }
    [Theory]
    [InlineData(false, true, false, 1)]
    [InlineData(true, true, false, 0)]
    [InlineData(false, false, false, 0)]
    public async Task EscapeHonoursBusyAndExplicitDismissalPolicy(bool busy, bool escape, bool backdrop, int expected)
    {
        var closes = 0;
        await using var page = await Harness.Open<ContentModal>(new() { ["Open"] = true, ["Title"] = "Edit", ["Busy"] = busy, ["CloseOnEscape"] = escape, ["CloseOnBackdrop"] = backdrop, ["OnClose"] = EventCallback.Factory.Create(new object(), () => closes++) });
        await page.Call(page.Root<ContentModal>(), "OnEscapeAsync"); Assert.Equal(expected, closes);
    }
    [Fact]
    public async Task BackdropDoesNotDiscardAFormByDefault()
    {
        var closes = 0;
        await using var page = await Harness.Open<ContentModal>(new() { ["Open"] = true, ["Title"] = "Edit", ["OnClose"] = EventCallback.Factory.Create(new object(), () => closes++) });
        await page.Call(page.Root<ContentModal>(), "BackdropAsync"); Assert.Equal(0, closes);
    }
    [Fact]
    public async Task BusyPreventsDuplicateSubmits()
    {
        var submits = 0;
        await using var page = await Harness.Open<ContentModal>(new() { ["Open"] = true, ["Title"] = "Edit", ["Busy"] = true, ["OnSubmit"] = EventCallback.Factory.Create(new object(), () => submits++) });
        await page.Call(page.Root<ContentModal>(), "SubmitAsync"); Assert.Equal(0, submits);
    }
    private sealed class Harness : IAsyncDisposable
    {
        private readonly Capture _capture = new();
        private ServiceProvider _services = null!; private HtmlRenderer _renderer = null!;
        private Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent _root;
        public T Root<T>() => _capture.Items.OfType<T>().First();
        public static async Task<Harness> Open<T>(Dictionary<string, object?>? parameters = null) where T : IComponent
        {
            var page = new Harness();
            page._services = new ServiceCollection().AddLogging().AddSingleton<IJSRuntime>(new TestJs()).AddSingleton<AuroraInterop>().AddSingleton<ToastService>()
                .AddSingleton<NavigationManager>(new Nav()).AddSingleton<IComponentActivator>(page._capture).BuildServiceProvider();
            page._renderer = new(page._services, page._services.GetRequiredService<ILoggerFactory>());
            page._root = await page._renderer.Dispatcher.InvokeAsync(() => page._renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters ?? new()))); return page;
        }
        public Task<string> Html() => _renderer.Dispatcher.InvokeAsync(_root.ToHtmlString);
        public T Value<T>(object component, string name) => (T)component.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(component)!;
        private static void Render(object component) => typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(component, null);
        public Task Set(object component, string name, object value) => _renderer.Dispatcher.InvokeAsync(() => { component.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, value); Render(component); });
        public Task Call(object component, string name, params object[] args) => _renderer.Dispatcher.InvokeAsync(async () => { var result = component.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(component, args); if (result is Task task) await task; Render(component); });
        public async ValueTask DisposeAsync() { await _renderer.DisposeAsync(); await _services.DisposeAsync(); }
    }
    private sealed class Capture : IComponentActivator { public List<IComponent> Items = []; public IComponent CreateInstance(Type type) { var c = (IComponent)Activator.CreateInstance(type)!; Items.Add(c); return c; } }
    private sealed class Nav : NavigationManager { public Nav() => Initialize("http://localhost/", "http://localhost/"); protected override void NavigateToCore(string uri, bool forceLoad) { } }
    private sealed class TestJs : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) => ValueTask.FromResult(id == "import" ? (T)(object)new Module() : default!);
        public ValueTask<T> InvokeAsync<T>(string id, CancellationToken ct, object?[]? args) => InvokeAsync<T>(id, args);
        private sealed class Module : IJSObjectReference { public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) => ValueTask.FromResult(default(T)!); public ValueTask<T> InvokeAsync<T>(string id, CancellationToken ct, object?[]? args) => InvokeAsync<T>(id, args); public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }
}
