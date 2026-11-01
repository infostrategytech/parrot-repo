using System.Reflection;
using Microsoft.AspNetCore.Components;
using Parrot.SharedComponents.Layout;
using Parrot.Web.Features.Auth;
using Parrot.Web.Features.Auth.Models;

namespace Test.Parrot.Api.Tests.Web;

// Every dashboard page says who may open it, so a page added later cannot slip
// through to a team member whose role does not reach it.
public class PageAccessTests
{
    // Open to every member on purpose: the overview and their own settings (the
    // business's tabs inside Settings are hidden from members separately).
    private static readonly HashSet<string> OpenToEveryMember = ["/dashboard", "/dashboard/settings", "/dashboard/settings/{Tab}"];

    private static IEnumerable<(Type Page, string Route)> DashboardPages() =>
        typeof(RequiresAccessAttribute).Assembly.GetTypes()
            .SelectMany(t => t.GetCustomAttributes<RouteAttribute>().Select(r => (t, r.Template)))
            .Where(p => p.Template.StartsWith("/dashboard", StringComparison.Ordinal) || p.Template.StartsWith("/ai-operations", StringComparison.Ordinal));

    [Fact]
    public void EveryDashboardPage_DeclaresWhoMayOpenIt()
    {
        List<string> missing = DashboardPages()
            .Where(p => !OpenToEveryMember.Contains(p.Route) && p.Page.GetCustomAttribute<RequiresAccessAttribute>() is null)
            .Select(p => $"{p.Page.Name} ({p.Route})")
            .ToList();

        Assert.True(missing.Count == 0, "Pages without [RequiresAccess]: " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryRuleAPageNames_Exists()
    {
        foreach ((Type page, _) in DashboardPages())
        {
            if (page.GetCustomAttribute<RequiresAccessAttribute>() is { } needs)
            {
                Assert.NotEmpty(AccessRules.For(needs.Rule));
            }
        }
    }

    [Fact]
    public void AConversationManager_ReachesTheInbox_ButNotAgentCreation_TheTeam_OrTheBusiness()
    {
        AuthResponse manager = new()
        {
            Role = "Conversation Manager",
            Permissions = ["conversations.view", "conversations.take-over", "conversations.reply", "conversations.close", "conversations.escalate", "conversations.logs"],
        };

        Assert.True(manager.Allows(AccessRules.Inbox));
        Assert.True(manager.Allows(AccessRules.Tasks));
        Assert.True(manager.Allows(AccessRules.Agents));
        Assert.False(manager.Allows(AccessRules.AgentCreate));
        Assert.False(manager.Allows(AccessRules.Channels));
        Assert.False(manager.Allows(AccessRules.Team));
        Assert.False(manager.Allows(AccessRules.Business));
    }

    [Fact]
    public void TheOwner_PassesEveryRule()
    {
        AuthResponse owner = new() { Role = "Owner" };

        Assert.True(owner.Allows(AccessRules.Team));
        Assert.True(owner.Allows(AccessRules.Business));
        Assert.True(owner.Allows(AccessRules.AgentCreate));
    }
}
