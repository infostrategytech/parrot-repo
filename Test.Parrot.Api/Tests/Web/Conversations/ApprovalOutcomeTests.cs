using Parrot.Application.Agents;
using Parrot.Web.Components.Pages.Dashboard.Conversations;

namespace Test.Parrot.Api.Tests.Web.Conversations;

// After Approve or Deny the card says what actually happened: sent, recorded for later, decided by someone else
// first, or no longer waiting at all.
public sealed class ApprovalOutcomeTests
{
    [Fact]
    public void Describe_ADecisionTheAgentActsOnNow_SaysItWasSent()
    {
        (string title, string note) = ApprovalOutcome.Describe(ApprovalOutcome.State("approved", "completed"), "completed", "Tolu");

        Assert.Equal("Approved", title);
        Assert.Equal("Decision sent. Waiting for Tolu to continue.", note);
    }

    [Fact]
    public void Describe_WhenTheAgentIsMidReply_SaysTheDecisionIsRecordedAndComingShortly()
    {
        (string title, string note) = ApprovalOutcome.Describe(
            ApprovalOutcome.State("denied", ApprovalDecisionStatus.ConversationBusy), ApprovalDecisionStatus.ConversationBusy, "Tolu");

        Assert.Equal("Denied", title);
        Assert.Equal("Decision recorded. Tolu is replying here right now and will act on it in a moment.", note);
    }

    [Fact]
    public void Describe_WhenNoTurnWasWaiting_SaysNothingMoreHappensHere()
    {
        (string title, string note) = ApprovalOutcome.Describe(
            ApprovalOutcome.State("approved", ApprovalDecisionStatus.NoTurn), ApprovalDecisionStatus.NoTurn, null);

        Assert.Equal("Approved", title);
        Assert.Equal("Decision recorded, but the agent had already moved on, so nothing more happens in this chat.", note);
    }

    // Not "Approved": this person's decision changed nothing.
    [Fact]
    public void Describe_WhenSomeoneDecidedFirst_DoesNotClaimThisPersonsDecision()
    {
        string state = ApprovalOutcome.State("approved", ApprovalDecisionStatus.AlreadyDecided);

        (string title, string note) = ApprovalOutcome.Describe(state, ApprovalDecisionStatus.AlreadyDecided, "Tolu");

        Assert.Equal(ApprovalOutcome.DecidedElsewhere, state);
        Assert.Equal("Already decided", title);
        Assert.Equal("Someone else decided this first. Nothing was changed.", note);
    }

    [Fact]
    public void Describe_WhenItWasNoLongerWaiting_SaysNothingChanged()
    {
        (string title, string note) = ApprovalOutcome.Describe(ApprovalOutcome.Gone, null, "Tolu");

        Assert.Equal("No longer waiting", title);
        Assert.Contains("Nothing was changed.", note);
    }

    [Fact]
    public void Describe_WithoutTheAgentsName_StartsASentenceWithACapital()
    {
        (_, string note) = ApprovalOutcome.Describe("approved", ApprovalDecisionStatus.ConversationBusy, " ");

        Assert.StartsWith("Decision recorded. The agent is replying", note);
    }
}
