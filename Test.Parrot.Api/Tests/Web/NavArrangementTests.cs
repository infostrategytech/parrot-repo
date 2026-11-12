using Parrot.SharedComponents.Layout;

namespace Test.Parrot.Api.Tests.Web;

// The rail's arrangement (designer, 11 November 2026): your work, your agents, their connections, then insights.
public class NavArrangementTests
{
    [Fact]
    public void TheRailReadsInFourGroupsWithAgentsInTheVendorsWord()
    {
        Assert.Equal(["Overview", "Inbox", "Tasks", "Customers", "Agents", "Knowledge", "Products & Services", "Channels", "Tools", "Insights"],
            NavMap.Primary.Select(entry => entry.Label));
        Assert.Equal(["Agents", "Channels", "Insights"], NavMap.Primary.Where(NavMap.StartsGroup).Select(entry => entry.Label));
        // The address stays, so links and bookmarks keep working.
        Assert.Equal("Agents", NavMap.Match("/dashboard/ai-operations/agent-1")?.Label);
    }
}
