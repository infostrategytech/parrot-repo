using Parrot.Web.Components.Pages.Dashboard.AiAgents;
using Parrot.Web.Features.Dashboard.AiAgents;

namespace Test.Parrot.Api.Tests.Web.AiAgents;

// Who an agent brings in, and who gets the tasks it raises for a person (designer, 10 November 2026).
public class HandoverRoutingTests
{
    private static readonly HandoverTeam Team = new(
    [
        new("owner", "You", true, null, null, true, []),
        new("chinedu", "Chinedu", false, "conversation-manager", "Customer service", true, []),
        new("funmi", "Funmi", false, "conversation-manager", "Customer service", true, ["instagram"]),
        new("tobi", "Tobi", false, "shop-assistant", "Shop assistant", false, []),
    ]);

    private static readonly string[] WhatsApp = ["whatsapp"];
    private static readonly string[] Both = ["whatsapp", "instagram"];

    [Fact]
    public void WorkingAloneEverythingGoesToTheOwner()
    {
        var alone = HandoverTeam.Alone("owner");
        Assert.True(alone.Solo);
        Assert.Equal("owner", HandoverRouting.Assign(new AgentSettings(), alone, WhatsApp, new Dictionary<string, int>())?.Id);
        Assert.Equal("you, in the Inbox", HandoverRouting.Phrase(new AgentSettings(), solo: true));
    }

    [Fact]
    public void AnyoneOnTheTeamIsTheDefaultAndWaitsForWhoeverIsFree()
    {
        var settings = new AgentSettings();
        Assert.Null(HandoverRouting.Assign(settings, Team, WhatsApp, new Dictionary<string, int>()));
        Assert.Equal("anyone on your team", HandoverRouting.Phrase(settings, solo: false));
    }

    [Fact]
    public void ARoleIsSharedEvenlyByOpenTasks()
    {
        var settings = new AgentSettings { HandoverMode = HandoverModes.Role, HandoverRoleId = "conversation-manager" };
        Assert.Equal("chinedu", HandoverRouting.Assign(settings, Team, Both, new Dictionary<string, int>())?.Id);
        Assert.Equal("funmi", HandoverRouting.Assign(settings, Team, Both, new Dictionary<string, int> { ["chinedu"] = 2, ["funmi"] = 1 })?.Id);
        // On a WhatsApp chat, only those who reply on WhatsApp.
        Assert.Equal("chinedu", HandoverRouting.Assign(settings, Team, Both, new Dictionary<string, int> { ["chinedu"] = 5 }, "whatsapp")?.Id);
        Assert.Equal("Customer service, shared evenly", HandoverRouting.Describe(HandoverModes.Role, Team, "conversation-manager", []));
    }

    [Fact]
    public void ChosenPeopleShareItAndSomeoneWhoCantReplyIsNeverChosen()
    {
        var settings = new AgentSettings { HandoverMode = HandoverModes.People, HandoverPeople = ["owner", "tobi"] };
        Assert.Equal("owner", HandoverRouting.Assign(settings, Team, WhatsApp, new Dictionary<string, int> { ["owner"] = 9 })?.Id);
        Assert.DoesNotContain(Team.Eligible(WhatsApp), p => p.Id == "tobi");
        Assert.DoesNotContain(Team.Eligible(WhatsApp), p => p.Id == "funmi");
        Assert.Equal("you and Tobi, shared evenly", HandoverRouting.Describe(HandoverModes.People, Team, null, ["owner", "tobi"]));
    }

    [Fact]
    public void WhenNoneOfThemCanTakeItItWaitsForAnyoneRatherThanNobody()
    {
        var settings = new AgentSettings { HandoverMode = HandoverModes.Role, HandoverRoleId = "shop-assistant" };
        Assert.Null(HandoverRouting.Assign(settings, Team, WhatsApp, new Dictionary<string, int>()));
        Assert.Single(Team.Roles(WhatsApp));
    }

    [Fact]
    public void AnOlderAgentKeepsItsOwnWordsUntilAChoiceIsMade()
    {
        var settings = new AgentSettings { HandoverTo = "Chioma on the front desk" };
        Assert.Equal("Chioma on the front desk", HandoverRouting.Phrase(settings, solo: false));
        Assert.Equal(["a"], new AgentSettings { HandoverPeople = ["a"] }.Copy().HandoverPeople);
    }
}
