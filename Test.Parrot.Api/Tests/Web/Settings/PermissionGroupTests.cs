using Parrot.Web.Features.Dashboard.Team;
using Xunit;

namespace Test.Parrot.Api.Tests.Web.Settings;

// A role's permissions grouped by the part of yára they open, in the sidebar's order (designer, 8 November 2026).
public sealed class PermissionGroupTests
{
    [Fact]
    public void PermissionsGroupByAreaInTheSidebarsOrderAndKeepTheirOwnOrder()
    {
        var groups = TeamWords.PermissionGroups(
        [
            new("analytics.dashboard", "View dashboard metrics"), new("conversations.view", "View conversations"),
            new("catalog.items.add", "Add items"), new("playbooks.create", "Create playbook"), new("agents.create", "Create AI agent"),
            new("conversations.reply", "Send manual replies"), new("channels.connect", "Connect channel"),
        ]);

        Assert.Equal(["Inbox", "Products & Services", "Channels", "AI agents", "Insights"], groups.Select(group => group.Title));
        Assert.Equal(["conversations.view", "conversations.reply"], groups[0].Permissions.Select(p => p.Key));
        Assert.Equal(["playbooks.create", "agents.create"], groups[3].Permissions.Select(p => p.Key));
    }

    [Fact]
    public void AnUnknownAreaStillShowsUnderOther()
    {
        var groups = TeamWords.PermissionGroups([new("conversations.view", "View conversations"), new("billing.view", "View billing")]);

        Assert.Equal(["Inbox", "Other"], groups.Select(group => group.Title));
    }
}
