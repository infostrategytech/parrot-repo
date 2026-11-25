using Parrot.Web.Components.Pages.Dashboard.Conversations;

namespace Test.Parrot.Api.Tests.Web.Conversations;

// The Answer dialog says what will happen before the answer goes, and the toast says what did: the agent replies to the
// customer, or the person replies themselves while they are in the chat.
public sealed class AgentAnswerOutcomeTests
{
    [Fact]
    public void Commitment_WhileTheAgentIsReplying_SaysItUsesTheAnswerToReply()
    {
        Assert.Equal("Cunha uses your answer to reply to Debbie.", AgentAnswerOutcome.Commitment("Cunha", "Debbie", personReplying: false));
    }

    [Fact]
    public void Commitment_WhileAPersonIsReplying_SaysTheAgentWontMessageTheCustomer()
    {
        Assert.Equal(
            "Cunha gets your answer but won't message Debbie while you're replying here. Send your reply yourself.",
            AgentAnswerOutcome.Commitment("Cunha", "Debbie", personReplying: true));
    }

    // With no name for the agent the sentence still starts with a capital.
    [Fact]
    public void Commitment_ForAnAgentWithNoName_StartsWithACapital()
    {
        Assert.StartsWith("The AI agent uses", AgentAnswerOutcome.Commitment("the AI agent", "Debbie", personReplying: false));
    }

    [Theory]
    [InlineData(true, true, "Cunha will reply to Debbie. Saved for next time.")]
    [InlineData(true, false, "Cunha will reply to Debbie.")]
    [InlineData(false, true, "Reply to Debbie yourself. Saved for next time.")]
    [InlineData(false, false, "Reply to Debbie yourself.")]
    public void Sent_SaysWhoRepliesAndWhetherItWasSaved(bool agentReplies, bool saved, string description)
    {
        (string title, string said) = AgentAnswerOutcome.Sent("Cunha", "Debbie", agentReplies, saved);

        Assert.Equal("Sent to Cunha.", title);
        Assert.Equal(description, said);
    }
}
