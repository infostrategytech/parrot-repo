using Parrot.Web.Components.Pages.Dashboard.AiAgents;

namespace Test.Parrot.Api.Tests.Web.AiAgents;

public class AgentCatalogAccessTests
{
    [Fact]
    public void TurningAccessOffClearsRecommendationsAndReopeningAccessDoesNotGrantThem()
    {
        var form = new AgentForm { Name = "Kora" };
        Assert.True(form.Settings.CanRecommend);
        var before = form.Snapshot();

        form.Settings.CanAccessProductsAndServices = false;
        form.Settings.CanRecommend = true;
        Assert.NotEqual(before, form.Snapshot());

        var reopened = AgentForm.From(form.ToAgent(live: false));
        Assert.False(reopened.Settings.CanAccessProductsAndServices);
        Assert.False(reopened.Settings.CanRecommend);

        reopened.Settings.CanAccessProductsAndServices = true;
        Assert.False(reopened.Settings.CanRecommend);
        reopened.Settings.CanRecommend = true;
        Assert.True(reopened.Settings.CanRecommend);
    }
}
