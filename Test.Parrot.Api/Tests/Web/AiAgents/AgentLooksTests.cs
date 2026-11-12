using Parrot.SharedComponents.DesignSystem;
using Parrot.Web.Components.Pages.Dashboard.AiAgents;
using Parrot.Web.Features.Dashboard.AiAgents;
using Xunit;

namespace Test.Parrot.Api.Tests.Web.AiAgents;

/// <summary>An agent's look is one of the twelve characters in any bright colour (designer, 10 November 2026).</summary>
public sealed class AgentLooksTests
{
    [Fact]
    public void EveryCharacterAndColourIsSavedAsShortTextAndReadBack()
    {
        foreach (var style in AvatarStyles.All)
            foreach (var tone in PresenceAvatarTones.Palette)
            {
                var saved = AgentLooks.Save(new AvatarAppearance(style.Direction, tone));
                Assert.StartsWith("look-", saved);
                Assert.True(saved.Length < 40);
                // The AI service's rule for a look key: letters, digits, '-' and '_' only.
                Assert.Matches("^[A-Za-z0-9_-]+$", saved);
                Assert.Equal(new AvatarAppearance(style.Direction, tone), AgentLooks.Of(saved));
            }
    }

    [Theory]
    [InlineData("Nova", PresenceAvatarDirection.Pip, PresenceAvatarTone.Yellow)]
    [InlineData("Atlas", PresenceAvatarDirection.Bloom, PresenceAvatarTone.Violet)]
    [InlineData("Bolt", PresenceAvatarDirection.Dot, PresenceAvatarTone.Indigo)]
    [InlineData("Aegis", PresenceAvatarDirection.Luma, PresenceAvatarTone.Fuchsia)]
    [InlineData("Buzz", PresenceAvatarDirection.Echo, PresenceAvatarTone.Pink)]
    [InlineData("Luna", PresenceAvatarDirection.Dash, PresenceAvatarTone.Lime)]
    public void AnAgentWithAnOldPictureKeepsALookOfItsOwnInThatPicturesColour(string mascot, PresenceAvatarDirection shape, PresenceAvatarTone tone) =>
        Assert.Equal(new AvatarAppearance(shape, tone), AgentLooks.Of(mascot));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("look:nobody:violet")]
    [InlineData("https://elsewhere.example/avatar.png")]
    public void AnythingUnknownShowsTheDefaultRatherThanBreaking(string? value) =>
        Assert.Equal(AvatarAppearance.Default, AgentLooks.Of(value));

    [Fact]
    public void AnUnknownColourKeepsTheShapeInItsOwnColour() =>
        Assert.Equal(AvatarAppearance.Suggested(PresenceAvatarDirection.Zig), AgentLooks.Of("look:zig:plaid"));

    [Fact]
    public void ANewAgentGetsACharacterNoOtherAgentWears()
    {
        Assert.Equal(AgentLooks.Save(AvatarAppearance.Suggested(PresenceAvatarDirection.Pip)), AgentLooks.Free([]));
        // "Nova" is Pip now, so the next agent is Bloom.
        Assert.Equal(PresenceAvatarDirection.Bloom, AgentLooks.Of(AgentLooks.Free(["Nova"])).Direction);
        Assert.Equal(PresenceAvatarDirection.Dash, AgentLooks.Of(AgentLooks.Free(["look:pip:red", "look:bloom:teal"])).Direction);
    }

    [Fact]
    public void TheApiKeepsACharacterLookAsItsTextAndAnOldMascotAsItsPicture()
    {
        var agent = new AiAgent { Name = "Ada", Description = "", Mascot = "look:fig:teal" };
        Assert.Equal("look:fig:teal", AgentMapping.ToInfo(agent).MascotUrl);
        Assert.Equal("look:fig:teal", AgentMapping.MascotOf("look:fig:teal"));
        agent.Mascot = "Luna";
        Assert.Equal("/assets/images/avatars/avatar-6.png", AgentMapping.ToInfo(agent).MascotUrl);
        Assert.Equal("Luna", AgentMapping.MascotOf("/assets/images/avatars/avatar-6.png"));
    }

    [Fact]
    public void ChoosingInThePickerSavesTheLook()
    {
        var form = AgentForm.Suggested([]);
        Assert.True(AgentLooks.IsLook(form.Mascot));
        form.ChooseLook(new AvatarAppearance(PresenceAvatarDirection.Ripple, PresenceAvatarTone.Orange));
        Assert.Equal("look-ripple-orange", form.Mascot);
    }

    [Fact]
    public void ALookSavedTheOldWayStillReadsAndIsSentInTheNewForm()
    {
        Assert.Equal(new AvatarAppearance(PresenceAvatarDirection.Fig, PresenceAvatarTone.Teal), AgentLooks.Of("look:fig:teal"));
        var agent = new AiAgent { Name = "Ada", Description = "", Mascot = "look:fig:teal" };
        Assert.Equal("look-fig-teal", OmniAgentMapping.ToUpdate(agent).Avatar);
        Assert.Equal("look-fig-teal", OmniAgentMapping.ToCreate(agent, []).Avatar);
        agent.Mascot = "Nova";
        Assert.Equal("Nova", OmniAgentMapping.ToUpdate(agent).Avatar);
    }
}
