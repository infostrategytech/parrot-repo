using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Parrot.SharedComponents.DesignSystem;
using Parrot.Web.Components.Layout.Landing;

// Real component event dispatch and rerenders, without a browser, demo dataset or live services.
// This verifies controlled behaviour, not visual layout, animation appearance or native keyboard feel.
var services = new ServiceCollection().AddLogging().AddSingleton<IJSRuntime, NoBrowserRuntime>().BuildServiceProvider();
await using var renderer = new FeatureRenderer(services, services.GetRequiredService<ILoggerFactory>());
var checks = 0;
void Check(bool value, string reason) { if (!value) throw new Exception(reason); checks++; }
await renderer.MountAsync<LandingBusinessKnowledge>();
Check(renderer.Attribute("knowledge-feature-preview", "data-feature") == "natural", "Tell it naturally is selected initially.");
foreach (var feature in new[] { "natural", "review", "update" })
{
    Check(renderer.ElementName("knowledge-feature-" + feature) == "button", "Each selector is a real keyboard-operable button.");
    Check(renderer.Attribute("knowledge-feature-" + feature, "aria-controls") == "knowledge-feature-preview", "Each selector identifies its external preview.");
}
foreach (var tone in new[] { "teal", "violet", "amber" }) Check(renderer.Elements().Any(e => e.Attributes.GetValueOrDefault("class")?.Contains("tone-" + tone) == true), "Each feature keeps its explicit icon accent.");
Check(System.Text.RegularExpressions.Regex.Matches(renderer.Text, "presence-avatar__spectacles").Count == 2, "Both teaching avatars wear glasses (including Razor's static SVG markup frames).");
for (var i = 0; i < 12; i++)
{
    var feature = new[] { "review", "update", "natural" }[i % 3];
    await renderer.ClickAsync("knowledge-feature-" + feature);
    Check(renderer.Attribute("knowledge-feature-preview", "data-feature") == feature, "A click changes the actual selected preview.");
    Check(renderer.Elements().Count(e => e.Attributes.GetValueOrDefault("aria-pressed") == "true") == 1, "Exactly one benefit is selected after each change.");
    Check(renderer.Attribute("knowledge-feature-" + feature, "aria-pressed") == "true", "The clicked control, not another loop item, is selected.");
    Check(renderer.Text.Contains("Your business.") && renderer.Text.Contains("Your way of working."), "The approved heading is preserved across interaction.");
    Check(renderer.Elements().Count(e => e.Attributes.GetValueOrDefault("class")?.Contains("ds-feature-selector__icon tone-") == true) == 3, "Selecting a feature does not remove the three icon accents.");
}
await renderer.ClickAsync("knowledge-feature-review");
await renderer.ClickAsync("knowledge-feature-review");
Check(renderer.Attribute("knowledge-feature-preview", "data-feature") == "review", "Choosing the current feature keeps a valid preview selected.");
Check(renderer.Elements().Count(e => e.Attributes.ContainsKey("onclick")) == 3, "Only the three selectors are interactive; the illustrated app controls are not.");
Check(renderer.Elements().Any(e => e.Attributes.GetValueOrDefault("role") == "status"), "The changed preview has an assistive-technology announcement.");
Check(!renderer.Text.Contains("See it in a reply") && !renderer.Elements().Any(e => e.Attributes.GetValueOrDefault("id") == "knowledge-examples-label"), "The competing Delivery/Payment/Returns switcher is gone.");
var browser = (NoBrowserRuntime)services.GetRequiredService<IJSRuntime>();
Check(browser.Calls.All(id => id is "teaching-module" or "bindTeachingScene" or "update"), "Only the decorative teaching lifecycle is called; no browser media, files or live services.");
await renderer.ClickAsync("knowledge-feature-natural");
var firstReplay = browser.Updates.Last().Replay;
await renderer.ClickAsync("knowledge-feature-natural");
Check(browser.Updates.Last().Active && browser.Updates.Last().Replay > firstReplay, "Clicking Tell it naturally again requests a replay.");
await renderer.ClickAsync("knowledge-feature-update");
Check(!browser.Updates.Last().Active, "Selecting another feature pauses the teaching scene.");
await using (var plain = new FeatureRenderer(services, services.GetRequiredService<ILoggerFactory>()))
{
    await plain.MountAsync<PresenceAvatar>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Paused"] = true }));
    Check(!plain.Text.Contains("presence-avatar__spectacles"), "Existing avatars do not gain glasses by default.");
}
await using (var human = new FeatureRenderer(services, services.GetRequiredService<ILoggerFactory>()))
{
    await human.MountAsync<LandingHumanControl>();
    Check(human.ElementName("you-stay-in-control") == "section", "Human control has a public section anchor.");
    Check(human.ElementName("human-control-title") == "h2", "The section follows the page heading hierarchy.");
    Check(human.ElementName("human-control-question") == "h3" && human.ElementName("human-control-takeover") == "h3", "Both outcomes have their own accessible heading.");
    Check(human.Text.Contains("Kora needs your answer") && human.Text.Contains("Inbox · Needs you"), "The question example stays in Inbox rather than Tasks.");
    Check(human.Text.Contains("You’re replying") && human.Text.Contains("Kora is paused."), "Taking over explicitly pauses the agent.");
    Check(human.Text.Contains("Answer") && human.Text.Contains("Done") && human.Text.Contains("Return to Kora"), "Illustrations match the existing app's control wording.");
    Check(!human.Elements().Any(e => e.Name is "button" or "input" or "select" or "textarea" || e.Attributes.ContainsKey("onclick")), "Public fragments contain no pretend working app controls.");
    Check(human.Elements().Count(e => e.Attributes.GetValueOrDefault("role") == "img" && !string.IsNullOrEmpty(e.Attributes.GetValueOrDefault("aria-label"))) == 2, "Both decorative examples have equivalent accessible descriptions.");
}
Console.WriteLine($"{checks} controlled landing component checks passed.");

sealed class NoBrowserRuntime : IJSRuntime
{
    public List<string> Calls { get; } = [];
    public List<(bool Active, int Replay)> Updates { get; } = [];
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
    {
        if (identifier != "import" || args?[0]?.ToString() != "./_content/Parrot.SharedComponents/js/avatar-teaching-scene.js") throw new InvalidOperationException("Unexpected browser access: " + identifier);
        Calls.Add("teaching-module");
        return ValueTask.FromResult((TValue)(object)new TeachingReference(this));
    }
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
}
sealed class TeachingReference(NoBrowserRuntime browser) : IJSObjectReference
{
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
    {
        browser.Calls.Add(identifier);
        if (identifier == "bindTeachingScene") return ValueTask.FromResult((TValue)(object)new TeachingReference(browser));
        if (identifier == "update") browser.Updates.Add(((bool)args![0]!, (int)args[1]!));
        return ValueTask.FromResult(default(TValue)!);
    }
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed record ElementInfo(string Name, Dictionary<string, string> Attributes, ulong Click);
sealed class FeatureRenderer(IServiceProvider services, ILoggerFactory loggerFactory) : Renderer(services, loggerFactory)
{
    private int _root;
    public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
    protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
    protected override void HandleException(Exception exception) => throw new InvalidOperationException("Component render failed.", exception);
    public Task MountAsync<T>(ParameterView? parameters = null) where T : IComponent => Dispatcher.InvokeAsync(async () =>
    {
        _root = AssignRootComponentId(InstantiateComponent(typeof(T)));
        await RenderRootComponentAsync(_root, parameters ?? ParameterView.Empty);
    });
    private IEnumerable<RenderTreeFrame> Frames(int id)
    {
        var frames = GetCurrentRenderTreeFrames(id);
        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames.Array[i];
            if (frame.FrameType == RenderTreeFrameType.Component)
            {
                foreach (var child in Frames(frame.ComponentId)) yield return child;
                i += frame.ComponentSubtreeLength - 1;
            }
            else yield return frame;
        }
    }
    public string Text => string.Join(" ", Frames(_root).Where(f => f.FrameType is RenderTreeFrameType.Text or RenderTreeFrameType.Markup).Select(f => f.FrameType == RenderTreeFrameType.Text ? f.TextContent : f.MarkupContent));
    public List<ElementInfo> Elements()
    {
        var frames = Frames(_root).ToArray();
        var elements = new List<ElementInfo>();
        for (var i = 0; i < frames.Length; i++)
        {
            if (frames[i].FrameType != RenderTreeFrameType.Element) continue;
            // HTML attribute names are case-insensitive, including Typography's splatted Id.
            var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); ulong click = 0;
            for (var j = i + 1; j < frames.Length && frames[j].FrameType == RenderTreeFrameType.Attribute; j++)
            {
                attrs[frames[j].AttributeName] = frames[j].AttributeValue?.ToString() ?? "";
                if (frames[j].AttributeName == "onclick") click = frames[j].AttributeEventHandlerId;
            }
            elements.Add(new(frames[i].ElementName, attrs, click));
        }
        return elements;
    }
    public string? Attribute(string id, string name) => Elements().Single(e => e.Attributes.GetValueOrDefault("id") == id).Attributes.GetValueOrDefault(name);
    public string ElementName(string id) => Elements().Single(e => e.Attributes.GetValueOrDefault("id") == id).Name;
    public Task ClickAsync(string id) => Dispatcher.InvokeAsync(() => DispatchEventAsync(Elements().Single(e => e.Attributes.GetValueOrDefault("id") == id).Click, null, new MouseEventArgs()));
}
