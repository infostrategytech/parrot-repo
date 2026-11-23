using Parrot.Web.Components.Pages.Dashboard.AiAgents;
using Parrot.Web.Services;

namespace Test.Parrot.Api.Tests.Web.AiAgents;

public class OrientationProgressWordsTests
{
    private static readonly OrientationTopic Prices = new("prices", "Prices", true, "pending_review", 3, 3);
    private static readonly OrientationTopic Delivery = new("delivery", "Delivery", true, "generating_review", 2, 2);
    private static readonly OrientationTopic Payment = new("payment_methods", "Payment methods", true, "in_progress", 1, 2);

    [Fact]
    public void NextStep_WhenAFormWaitsAndIsShown_PutsTheOwnersTurnFirst()
    {
        OrientationProgress progress = Snapshot(pending: ["prices"], generating: ["delivery"], active: Payment);

        string? next = OrientationProgressWords.NextStep(progress, formShown: true, inCall: false);

        Assert.Equal("Your turn: approve the form below to finish Prices.", next);
    }

    [Fact]
    public void NextStep_WhenAFormWaitsDuringAVoiceChat_SaysToEndTheChatToSeeIt()
    {
        OrientationProgress progress = Snapshot(pending: ["prices"]);

        string? next = OrientationProgressWords.NextStep(progress, formShown: false, inCall: true);

        Assert.Equal("A form is waiting for you. End the voice chat to see it.", next);
    }

    [Fact]
    public void NextStep_WhenAFormWaitsButHasNotArrived_SaysItIsOnItsWay()
    {
        OrientationProgress progress = Snapshot(pending: ["prices"]);

        string? next = OrientationProgressWords.NextStep(progress, formShown: false, inCall: false);

        Assert.Equal("Your form for Prices is on its way.", next);
    }

    [Fact]
    public void NextStep_WhenFormsAreBeingWritten_NamesEveryTopic()
    {
        OrientationProgress progress = Snapshot(generating: ["prices", "delivery", "payment_methods"], active: Payment);

        string? next = OrientationProgressWords.NextStep(progress, formShown: false, inCall: false);

        Assert.Equal("Preparing a form for you to check Prices, Delivery and Payment methods…", next);
    }

    [Fact]
    public void NextStep_WhenATopicIsUnderWay_NamesItAndHowManyDetailsAreCovered()
    {
        OrientationProgress progress = Snapshot(active: new OrientationTopic("payment_methods", "Payment methods", true, null, 1, 2));

        string? next = OrientationProgressWords.NextStep(progress, formShown: false, inCall: false);

        Assert.Equal("Now: Payment methods, 1 of 2 details", next);
    }

    [Fact]
    public void NextStep_WhenATopicIsNotInTheList_ReadsItsKey()
    {
        OrientationProgress progress = new(30, false, 7, 1, PendingReview: ["opening_hours"]);

        string? next = OrientationProgressWords.NextStep(progress, formShown: true, inCall: false);

        Assert.Equal("Your turn: approve the form below to finish opening hours.", next);
    }

    [Fact]
    public void NextStep_WhenReadyOrNothingIsKnown_SaysNothing()
    {
        Assert.Null(OrientationProgressWords.NextStep(new OrientationProgress(100, true, 7, 7, PendingReview: ["prices"]), true, false));
        Assert.Null(OrientationProgressWords.NextStep(new OrientationProgress(0, false, 7, 0), false, false));
    }

    [Theory]
    [InlineData("reviewed", 2, 2, "Done")]
    [InlineData("pending_review", 3, 3, "Waiting for you to approve")]
    [InlineData("generating_review", 2, 2, "Preparing your form")]
    [InlineData("in_progress", 1, 2, "In progress, 1 of 2 details")]
    [InlineData("in_progress", 0, 0, "In progress")]
    [InlineData("not_started", 0, 2, "Not started")]
    [InlineData(null, 0, 0, "Not started")]
    public void Status_ForEachServiceStatus_SaysItPlainly(string? status, int covered, int total, string expected)
    {
        Assert.Equal(expected, OrientationProgressWords.Status(new OrientationTopic("prices", "Prices", true, status, covered, total)));
    }

    [Fact]
    public void Status_WhenTheTopicIsOptional_SaysSo()
    {
        Assert.Equal("Not started · optional", OrientationProgressWords.Status(new OrientationTopic("faq", "FAQ", false, "not_started")));
    }

    [Fact]
    public void Status_WhenMoreDetailsAreCoveredThanCounted_NeverSaysMoreThanAll()
    {
        Assert.Equal("In progress, 2 of 2 details", OrientationProgressWords.Status(new OrientationTopic("prices", "Prices", true, "in_progress", 3, 2)));
    }

    [Fact]
    public void Label_UntilReady_CountsTopicsAndPercent_ThenSaysItIsDone()
    {
        Assert.Equal("2 of 7 topics done · 42%", OrientationProgressWords.Label(new OrientationProgress(42, false, 7, 2), "Kemi"));
        Assert.Equal("Done. Kemi knows enough to start.", OrientationProgressWords.Label(new OrientationProgress(100, true, 7, 7), "Kemi"));
    }

    [Theory]
    [InlineData(2, "Finish teaching Kemi first: 5 topics left.")]
    [InlineData(6, "Finish teaching Kemi first: 1 topic left.")]
    // Every topic reviewed but the service not letting it on yet: nothing made up about what's left.
    [InlineData(7, "Finish teaching Kemi first.")]
    public void NotReady_CountsTheRequiredTopicsLeft(int reviewed, string expected)
    {
        Assert.Equal(expected, OrientationProgressWords.NotReady(new OrientationProgress(42, false, 7, reviewed), "Kemi"));
    }

    private static OrientationProgress Snapshot(string[]? pending = null, string[]? generating = null, OrientationTopic? active = null) =>
        new(42, false, 7, 2, pending ?? [], generating ?? [], active, [Prices, Delivery, Payment]);
}
