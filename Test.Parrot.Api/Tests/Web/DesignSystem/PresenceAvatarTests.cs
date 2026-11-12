using System.Net;
using System.Text.RegularExpressions;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Parrot.SharedComponents.DesignSystem;
using Parrot.SharedComponents.Services;
using Parrot.Web.Services;
using Xunit;

namespace Test.Parrot.Api.Tests.Web.DesignSystem;

public sealed class PresenceAvatarTests
{
    public static IEnumerable<object[]> Combinations =>
        from direction in Enum.GetValues<PresenceAvatarDirection>()
        from state in Enum.GetValues<PresenceAvatarState>()
        select new object[] { direction, state };

    [Theory, MemberData(nameof(Combinations))]
    public async Task EveryCharacterAndStateHasAScalableAccessiblePresentation(PresenceAvatarDirection direction, PresenceAvatarState state)
    {
        var html = await Render<PresenceAvatar>(new()
        {
            [nameof(PresenceAvatar.Direction)] = direction,
            [nameof(PresenceAvatar.State)] = state,
        });
        Assert.Contains("viewBox=\"0 0 128 128\"", html);
        Assert.Contains($"data-state=\"{state.ToString().ToLowerInvariant()}\"", html);
        Assert.Contains("role=\"img\"", html);
        Assert.Contains($"aria-label=\"{direction}, {PresenceAvatarWords.Label(state).ToLowerInvariant()}\"", WebUtility.HtmlDecode(html));
        Assert.Contains("focusable=\"false\"", html);
        Assert.DoesNotContain("<button", html); Assert.DoesNotContain("<animate", html);
        Assert.Equal(state is PresenceAvatarState.Done or PresenceAvatarState.Blocked or PresenceAvatarState.Waiting, html.Contains("class=\"presence-avatar__badge\""));
    }

    [Theory]
    [InlineData(PresenceAvatarSize.Small)]
    [InlineData(PresenceAvatarSize.Medium)]
    [InlineData(PresenceAvatarSize.Large)]
    public async Task SizesUseTheSameIllustrationAndCanBeDecorative(PresenceAvatarSize size)
    {
        var html = await Render<PresenceAvatar>(new()
        {
            [nameof(PresenceAvatar.Size)] = size,
            [nameof(PresenceAvatar.Decorative)] = true,
            [nameof(PresenceAvatar.Paused)] = true,
        });
        Assert.Contains($"presence-avatar--{size.ToString().ToLowerInvariant()}", html);
        Assert.Contains("is-paused", html); Assert.Contains("aria-hidden=\"true\"", html);
        Assert.DoesNotContain("aria-label=", html); Assert.DoesNotContain("role=\"img\"", html);
    }

    [Theory]
    [InlineData(PresenceAvatarTone.Violet)]
    [InlineData(PresenceAvatarTone.Coral)]
    [InlineData(PresenceAvatarTone.Green)]
    [InlineData(PresenceAvatarTone.Sky)]
    [InlineData(PresenceAvatarTone.Amber)]
    public async Task EveryColourIsFlatAndNeedsNoSharedPaintReferences(PresenceAvatarTone tone)
    {
        var values = new Dictionary<string, object?> { [nameof(PresenceAvatar.Tone)] = tone };
        var one = await Render<PresenceAvatar>(values);
        Assert.DoesNotContain("<defs", one); Assert.DoesNotContain("url(#", one);
        Assert.DoesNotContain("Gradient", one); Assert.DoesNotContain("presence-avatar__mouth", one);
        Assert.Equal(2, Regex.Matches(one, "class=\"presence-avatar__pupil\"").Count);
        Assert.Equal(1, Regex.Matches(one, "class=\"presence-avatar__body\"").Count);
        Assert.Contains($"presence-avatar--{tone.ToString().ToLowerInvariant()}", one);
    }

    [Fact]
    public async Task AnAgentNameAndStateRemainTextuallyAvailable()
    {
        var html = await Render<PresenceAvatar>(new()
        {
            [nameof(PresenceAvatar.Name)] = "Kora",
            [nameof(PresenceAvatar.State)] = PresenceAvatarState.Blocked,
        });
        Assert.Contains("aria-label=\"Kora, needs help\"", html);
        Assert.Equal("Status unavailable", PresenceAvatarWords.Label((PresenceAvatarState)99));
    }

    [Fact]
    public async Task TheCompactPickerShowsOnlyCharactersAndColoursUnderAPreview()
    {
        // Designer, 10 November 2026: the tile grid was too much for setup and small screens.
        var html = WebUtility.HtmlDecode(await Render<AgentAvatarChooser>(new() { [nameof(AgentAvatarChooser.Compact)] = true }));
        Assert.Contains("agent-avatar-chooser is-compact", html);
        Assert.Equal(12, Regex.Matches(html, "class=\"ds-row-button avatar-shape").Count);
        Assert.Equal(1, Regex.Matches(html, "avatar-shape is-selected").Count);
        Assert.Equal(PresenceAvatarTones.Palette.Count, Regex.Matches(html, "class=\"ds-row-button avatar-swatch ").Count);
        Assert.DoesNotContain("agent-avatar-tile", html); Assert.DoesNotContain("Make it yours", html);
        foreach (var style in AvatarStyles.All) Assert.Contains($"aria-label=\"{style.Name}\"", html);
    }

    [Fact]
    public async Task EveryAvatarHasWhiteEyesWithAPupilInside()
    {
        var whites = await Render<PresenceAvatar>([]);
        Assert.Equal(2, Regex.Matches(whites, "class=\"presence-avatar__white\"").Count);
        Assert.Equal(2, Regex.Matches(whites, "class=\"presence-avatar__pupil\"[^>]*r=\"7\"").Count);
        Assert.Equal(2, Regex.Matches(whites, "class=\"presence-avatar__eyeball\"").Count);
    }

    [Fact]
    public void CollectionContainsTwelveUniqueStableIdentitiesAndOriginalSilhouettes()
    {
        Assert.Equal(12, AvatarStyles.All.Count);
        Assert.Equal(12, AvatarStyles.All.Select(s => s.Id).Distinct().Count());
        Assert.Equal(12, AvatarStyles.All.Select(s => s.BodyPath).Distinct().Count());
        Assert.Equal(Enum.GetValues<PresenceAvatarDirection>().Order(), AvatarStyles.All.Select(s => s.Direction).Order());
        foreach (var style in AvatarStyles.All)
        {
            Assert.Equal(style, AvatarStyles.Of(style.Id)); Assert.Equal(style, AvatarStyles.Of(style.Direction));
            Assert.Equal(style.Id, AvatarAppearance.Suggested(style.Direction).StyleId);
            Assert.StartsWith("M", style.BodyPath); Assert.EndsWith("Z", style.BodyPath);
            Assert.Contains("translate(", style.FacePosition);
        }
        Assert.Equal(AvatarStyles.All[0], AvatarStyles.Of("unrecognised-style"));
    }

    [Fact]
    public async Task PickerShowsExactlyOneCharacterAndOneColourSelectedWithEveryThumbnailAlive()
    {
        var html = WebUtility.HtmlDecode(await Render<AgentAvatarChooser>(new()
        {
            [nameof(AgentAvatarChooser.Value)] = new AvatarAppearance(PresenceAvatarDirection.Fig, PresenceAvatarTone.Sky),
        }));
        Assert.Equal(12, Regex.Matches(html, "class=\"presence-avatar ").Count);
        Assert.Equal(12, Regex.Matches(html, "is-reactive").Count);
        Assert.DoesNotContain("is-paused", html);
        Assert.Equal(PresenceAvatarTones.Palette.Count, Regex.Matches(html, "class=\"ds-row-button avatar-swatch ").Count);
        Assert.Equal(1, Regex.Matches(html, "agent-avatar-tile is-selected").Count);
        Assert.Equal(2, Regex.Matches(html, "aria-pressed=\"true\"").Count);
        Assert.Contains("Fig avatar\" aria-pressed=\"true\"", html);
        Assert.Equal(1, Regex.Matches(html, "agent-avatar-tile__check").Count);
    }

    [Fact]
    public async Task ChoosingACharacterEmitsItsCuratedColourWithoutMutatingTheControlledValue()
    {
        AvatarAppearance? received = null;
        var picker = new AgentAvatarChooser { Value = new(PresenceAvatarDirection.Pip, PresenceAvatarTone.Sky),
            ValueChanged = EventCallback.Factory.Create<AvatarAppearance>(this, value => received = value) };
        await Call(picker, "ChooseAsync", PresenceAvatarDirection.Bloom);
        Assert.Equal(AvatarAppearance.Suggested(PresenceAvatarDirection.Bloom), received);
        Assert.Equal(PresenceAvatarDirection.Pip, picker.Value.Direction);
        received = null; await Call(picker, "ChooseAsync", PresenceAvatarDirection.Pip);
        Assert.Null(received); // Clicking the chosen style doesn't reset a custom colour.
    }

    [Fact]
    public async Task OnceAColourIsChosenSwitchingShapeKeepsIt()
    {
        AvatarAppearance? received = null;
        var picker = new AgentAvatarChooser { Value = new(PresenceAvatarDirection.Pip, PresenceAvatarTone.Violet),
            ValueChanged = EventCallback.Factory.Create<AvatarAppearance>(this, value => received = value) };
        await Call(picker, "ColourAsync", PresenceAvatarTone.Pink);
        picker.Value = received!;
        await Call(picker, "ChooseAsync", PresenceAvatarDirection.Bloom);
        Assert.Equal(new AvatarAppearance(PresenceAvatarDirection.Bloom, PresenceAvatarTone.Pink), received);
    }

    [Fact]
    public void ThePaletteOffersEveryBrightColourOnceRoundTheWheel()
    {
        Assert.Equal(Enum.GetValues<PresenceAvatarTone>().Order(), PresenceAvatarTones.Palette.Order());
        Assert.Equal(14, PresenceAvatarTones.Palette.Distinct().Count());
    }

    [Theory]
    [InlineData(PresenceAvatarTone.Violet)]
    [InlineData(PresenceAvatarTone.Coral)]
    [InlineData(PresenceAvatarTone.Green)]
    [InlineData(PresenceAvatarTone.Sky)]
    [InlineData(PresenceAvatarTone.Amber)]
    [InlineData(PresenceAvatarTone.Pink)]
    [InlineData(PresenceAvatarTone.Lime)]
    public async Task ChoosingAColourPreservesTheCharacter(PresenceAvatarTone tone)
    {
        var initialTone = tone == PresenceAvatarTone.Violet ? PresenceAvatarTone.Coral : PresenceAvatarTone.Violet;
        AvatarAppearance? received = null;
        var picker = new AgentAvatarChooser { Value = new(PresenceAvatarDirection.Echo, initialTone),
            ValueChanged = EventCallback.Factory.Create<AvatarAppearance>(this, value => received = value) };
        await Call(picker, "ColourAsync", tone);
        Assert.Equal(new AvatarAppearance(PresenceAvatarDirection.Echo, tone), received);
    }

    [Fact]
    public async Task DisabledPickerCannotChangeTheChoiceEvenThroughACallback()
    {
        var calls = 0;
        var picker = new AgentAvatarChooser { Disabled = true,
            ValueChanged = EventCallback.Factory.Create<AvatarAppearance>(this, _ => calls++) };
        await Call(picker, "ChooseAsync", PresenceAvatarDirection.Nest);
        await Call(picker, "ColourAsync", PresenceAvatarTone.Green); Assert.Equal(0, calls);
    }

    private static Task Call(AgentAvatarChooser picker, string method, object value) =>
        (Task)typeof(AgentAvatarChooser).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(picker, [value])!;

    internal static async Task<string> Render<T>(Dictionary<string, object?> values, bool local = false) where T : IComponent
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<NavigationManager, LocalNavigation>();
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        services.AddScoped<AuroraInterop>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(values))).ToHtmlString());
    }

    private sealed class LocalNavigation : NavigationManager
    {
        public LocalNavigation() => Initialize("http://localhost/", "http://localhost/design-system/avatar-playground");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }

    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new InvalidOperationException("Static rendering must not run JavaScript.");
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }
}
