using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Parrot.SharedComponents.DesignSystem;
using Parrot.SharedComponents.Services;
using Parrot.Web.Components.Pages.Dashboard.Team;
using Parrot.Web.Features.Dashboard.Team;

namespace Test.Parrot.Api.Tests.Web.Settings;

public sealed class TeamChannelTooltipTests
{
    [Fact]
    public async Task TooltipHasAnAssociatedDescriptionWithoutAnExtraDefaultTabStop()
    {
        var html = await Render<Tooltip>(new() { ["Text"] = "Helpful detail", ["ChildContent"] = Content("Visible label") });
        var id = Regex.Match(html, "aria-describedby=\"([^\"]+)").Groups[1].Value;
        Assert.NotEmpty(id); Assert.Contains($"id=\"{id}\" role=\"tooltip\"", html);
        Assert.Contains("popover=\"manual\" hidden", html); Assert.Contains("Helpful detail", html);
        Assert.DoesNotContain("tabindex=", html); Assert.DoesNotContain("title=", html);
    }

    [Fact]
    public async Task InformationalMarksCanBeReachedByKeyboardAndIdsDoNotCollide()
    {
        var parameters = new Dictionary<string, object?> { ["Text"] = "WhatsApp", ["Focusable"] = true, ["ChildContent"] = Content("Mark") };
        var first = await Render<Tooltip>(parameters); var second = await Render<Tooltip>(parameters);
        Assert.Contains("tabindex=\"0\"", first);
        Assert.NotEqual(Regex.Match(first, "aria-describedby=\"([^\"]+)").Value, Regex.Match(second, "aria-describedby=\"([^\"]+)").Value);
    }

    [Fact]
    public async Task EmptyHelpDoesNotCreateAnEmptyPopupOrTabStop()
    {
        var html = await Render<Tooltip>(new() { ["Text"] = " ", ["Focusable"] = true, ["ChildContent"] = Content("Label") });
        Assert.DoesNotContain("role=\"tooltip\"", html); Assert.DoesNotContain("aria-describedby", html); Assert.DoesNotContain("tabindex=", html);
    }

    [Fact]
    public async Task MarksUseSharedHelpAndKeepCustomChannelNamesWithoutNativeTitleOverlap()
    {
        var html = await Render<ChannelMarks>(new() { ["Channels"] = new[] { "whatsapp", "facebook" }, ["Name"] = (Func<string, string>)(key => key == "facebook" ? "Messenger" : "WhatsApp") });
        Assert.Equal(2, Regex.Matches(html, "role=\"tooltip\"").Count); Assert.Equal(2, Regex.Matches(html, "tabindex=\"0\"").Count);
        Assert.Contains("Messenger", html); Assert.Contains("WhatsApp, Messenger", html);
        Assert.DoesNotContain("title=", html); Assert.Contains("channel-glyph--whatsapp", html); Assert.Contains("channel-glyph--facebook", html);
    }

    [Fact]
    public async Task OverflowNamesTheRemainingChannelsAndGuardsAnInvalidLimit()
    {
        var parameters = new Dictionary<string, object?> { ["Channels"] = new[] { "whatsapp", "instagram", "facebook", "tiktok" }, ["Max"] = 2 };
        var html = System.Net.WebUtility.HtmlDecode(await Render<ChannelMarks>(parameters));
        Assert.Equal(2, Regex.Matches(html, "role=\"tooltip\"").Count); Assert.Contains("+3", html); Assert.Contains("Instagram, Facebook, TikTok", html);
        parameters["Max"] = 0; html = System.Net.WebUtility.HtmlDecode(await Render<ChannelMarks>(parameters));
        Assert.Single(Regex.Matches(html, "role=\"tooltip\"").Cast<Match>()); Assert.Contains("+4", html);
    }

    [Fact]
    public async Task TeamHeadingSaysWhatTheChannelMarksRepresent()
    {
        var html = await Render<TeamRow>(new() { ["Head"] = true });
        Assert.Contains("Channel access", html);
    }

    [Fact]
    public async Task OwnerAlwaysShowsUnrestrictedAccessRatherThanAConnectedChannelSnapshot()
    {
        var html = await Member(owner: true, grants: []);
        Assert.Contains("All channels", html); Assert.Contains("Access to current and future channels", html);
        Assert.DoesNotContain("No access", html); Assert.DoesNotContain("channel-glyph--", html);
    }

    [Fact]
    public async Task AnEmptyChannelSelectionWithinAGrantMeansCurrentAndFutureChannels()
    {
        var html = await Member(grants: [Grant(null)]);
        Assert.Contains("All channels", html); Assert.Contains("Access to current and future channels", html);
        Assert.DoesNotContain("channel-glyph--", html);
    }

    [Fact]
    public async Task NoGrantsMustNotBeMisrepresentedAsAllChannelAccess()
    {
        var html = await Member(grants: []);
        Assert.Contains("No access", html); Assert.DoesNotContain("All channels", html);
    }

    [Fact]
    public async Task SelectedChannelsRemainSelectedEvenIfTheyIncludeEveryCurrentOption()
    {
        var html = await Member(grants: [Grant(null, "whatsapp", "instagram")]);
        Assert.Contains("channel-glyph--whatsapp", html); Assert.Contains("channel-glyph--instagram", html);
        Assert.DoesNotContain("All channels", html); Assert.Equal(2, Regex.Matches(html, "role=\"tooltip\"").Count);
    }

    [Fact]
    public async Task DifferentWorkspaceAccessIsNotPresentedAsAUnionOfAllowedChannels()
    {
        var html = await Member(grants: [Grant("workspace-a", "whatsapp"), Grant("workspace-b", "instagram")]);
        Assert.Contains("Differs by workspace", html); Assert.DoesNotContain("channel-glyph--", html); Assert.DoesNotContain("All channels", html);
    }

    [Fact]
    public async Task PendingInvitesUseTheSameAccessLanguage()
    {
        var html = await Render<TeamRow>(new() { ["Invite"] = new TeamInvite(Guid.NewGuid(), "Controlled invite", "invite@example.test", [Grant(null)], DateTime.UtcNow, DateTime.UtcNow.AddDays(7)) });
        Assert.Contains("All channels", html); Assert.Contains("Access to current and future channels", html);
    }

    private static TeamGrant Grant(string? workspace, params string[] channels) => new(workspace, "controlled-role", "Controlled role", false, channels);
    private static Task<string> Member(bool owner = false, IReadOnlyList<TeamGrant>? grants = null) => Render<TeamRow>(new()
    {
        ["Member"] = new TeamMember("controlled-person", "Controlled person", "person@example.test", owner, grants ?? [Grant(null)], DateTime.UtcNow),
        ["ChannelOptions"] = new TeamOption[] { new("whatsapp", "WhatsApp"), new("instagram", "Instagram") }
    });
    private static RenderFragment Content(string text) => builder => builder.AddContent(0, text);

    private static async Task<string> Render<T>(Dictionary<string, object?> parameters) where T : IComponent
    {
        await using var services = new ServiceCollection().AddLogging().AddSingleton<NavigationManager>(new TestNavigation()).AddSingleton<IJSRuntime>(new NoJs()).AddSingleton<AuroraInterop>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters));
            return root.ToHtmlString();
        });
    }
    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string id, CancellationToken ct, object?[]? args) => InvokeAsync<T>(id, args);
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/dashboard/settings/team");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }
}
