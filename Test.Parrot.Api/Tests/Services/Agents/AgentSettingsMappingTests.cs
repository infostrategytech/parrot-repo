using System.Text.Json;
using Parrot.Application.Agents;
using Parrot.Application.DTOs.Agents;
using Parrot.Web.Components.Pages.Dashboard.AiAgents;
using Parrot.Web.Features.Dashboard.AiAgents;
using Parrot.Web.Services;

namespace Test.Parrot.Api.Tests.Services.Agents;

// The settings travel screen -> API -> column -> orchestrator and back; a
// field that does not survive the round trip is one the owner set for nothing.
public class AgentSettingsMappingTests
{
    private static AgentSettingsDto Dto() => new()
    {
        Role = "Custom",
        CustomRole = "Bookings",
        HandoverTo = "Chioma on the front desk",
        CanOfferDiscounts = true,
        CanApproveRefunds = true,
        Tone = "Professional",
        Length = "Concise",
        Emoji = "None",
        Greeting = "Welcome to Ada's Kitchen!",
        AvoidPhrases = ["no wahala", "cheap"],
        CanRecommend = false,
        CanCheckOrders = true,
        CanTakeDetails = false,
        HandOverWhenUpset = false,
        HandOverAfterMisses = false,
        HandOverBigOrders = true,
        BigOrderNaira = 120000,
        Notes = "We close on Sundays.",
        LeftOut = ["Prices", "Returns"],
    };

    [Fact]
    public void AgentSettingsJson_RoundTrip_KeepsEveryField()
    {
        string column = AgentSettingsJson.Write(Dto());

        AgentSettingsDto read = AgentSettingsJson.Read(column);

        Assert.Equal(JsonSerializer.Serialize(Dto(), AgentSettingsJson.Options), JsonSerializer.Serialize(read, AgentSettingsJson.Options));
    }

    [Fact]
    public void AgentSettingsJson_Write_UsesTheOrchestratorsNames()
    {
        using JsonDocument json = JsonDocument.Parse(AgentSettingsJson.Write(Dto()));

        Assert.Equal(120000, json.RootElement.GetProperty("bigOrderNaira").GetInt32());
        Assert.Equal("Chioma on the front desk", json.RootElement.GetProperty("handoverTo").GetString());
        Assert.Equal("Prices", json.RootElement.GetProperty("leftOut")[0].GetString());
        Assert.True(json.RootElement.GetProperty("handOverBigOrders").GetBoolean());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public void AgentSettingsJson_Read_WithAColumnThatIsNotTheShape_ReturnsTheDefaults(string? column)
    {
        AgentSettingsDto read = column == "[1,2]"
            ? Assert.IsType<AgentSettingsDto>(ReadOrDefault(column))
            : AgentSettingsJson.Read(column);

        Assert.Equal("Support", read.Role);
        Assert.Equal(50000, read.BigOrderNaira);
        Assert.True(read.CanRecommend);
    }

    private static AgentSettingsDto ReadOrDefault(string column)
    {
        try
        {
            return AgentSettingsJson.Read(column);
        }
        catch (JsonException)
        {
            return new AgentSettingsDto();
        }
    }

    [Fact]
    public void AgentMapping_RoundTrip_KeepsSettingsChannelsAndMascot()
    {
        AiAgent agent = new()
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Luna",
            Description = "Takes bookings.",
            Mascot = "Luna",
            HumanTakeover = true,
            IsDraft = true,
            IsActive = false,
            Channels = AgentBuilderCopy.DefaultChannels(["whatsapp", "instagram"]),
            Settings = AgentMapping.FromDto(Dto()),
        };

        AgentInfo info = AgentMapping.ToInfo(agent);
        // What the API would answer with, had it saved the request.
        AgentResponse response = new()
        {
            Id = Guid.Parse(agent.Id),
            Name = info.Name,
            Description = info.Description,
            MascotUrl = info.MascotUrl,
            SupportsHumanTakeover = info.SupportsHumanTakeover,
            IsActive = !info.IsDraft,
            IsDraft = info.IsDraft,
            Channels = info.Channels!,
            Settings = AgentSettingsJson.Read(AgentSettingsJson.Write(info.Settings)),
            Version = 1,
        };
        AiAgent back = AgentMapping.ToAgent(response);

        Assert.Equal(agent.Id, back.Id);
        Assert.Equal("Luna", back.Mascot);
        Assert.Equal("/assets/images/avatars/avatar-6.png", info.MascotUrl);
        Assert.True(back.IsDraft);
        Assert.False(back.IsActive);
        Assert.Equal(["whatsapp", "instagram"], info.Channels);
        Assert.Equal(["whatsapp", "instagram"], back.Channels.Where(c => c.IsEnabled).Select(c => c.Key));
        Assert.Equal(AgentRoleTemplate.Custom, back.Settings.Role);
        Assert.Equal("Bookings", back.Settings.CustomRole);
        Assert.Equal(AgentVoicePreset.Professional, back.Settings.Tone);
        Assert.Equal(AgentResponseLength.Concise, back.Settings.Length);
        Assert.Equal(AgentEmojiUsage.None, back.Settings.Emoji);
        Assert.Equal(["no wahala", "cheap"], back.Settings.AvoidPhrases);
        Assert.Equal([KnowledgeTopic.Prices, KnowledgeTopic.Returns], back.Settings.LeftOut);
        Assert.Equal(120000, back.Settings.BigOrderNaira);
        Assert.False(back.Settings.CanRecommend);
        Assert.True(back.Settings.HandOverBigOrders);
        Assert.Equal("We close on Sundays.", back.Settings.Notes);
    }

    [Fact]
    public void AgentMapping_FromDto_WithNamesTheScreensDoNotKnow_FallsBackToTheDefaults()
    {
        AgentSettings settings = AgentMapping.FromDto(new AgentSettingsDto { Role = "Concierge", Tone = "shouty", LeftOut = ["Weather", "prices"] });

        Assert.Equal(AgentRoleTemplate.Support, settings.Role);
        Assert.Equal(AgentVoicePreset.Warm, settings.Tone);
        Assert.Equal([KnowledgeTopic.Prices], settings.LeftOut);
    }

    [Fact]
    public void AgentMapping_MascotOf_ReadsTheNameFromItsImageOrItself()
    {
        Assert.Equal("Atlas", AgentMapping.MascotOf("/assets/images/avatars/avatar-2.png"));
        Assert.Equal("Bolt", AgentMapping.MascotOf("bolt"));
        Assert.Equal("Nova", AgentMapping.MascotOf("https://elsewhere.example/avatar.png"));
    }
}
