using Parrot.Web.Components.Pages.Dashboard.Conversations;
using Parrot.Web.Services;

namespace Test.Parrot.Api.Tests.Web.Conversations;

// Needs you shows an agent's open question or approval to whoever can answer it, until conversations can be
// assigned; a person named on the conversation still owns it.
public sealed class InboxAttentionRulesTests
{
    private const string Me = "person-me";
    private const string Other = "person-other";

    private static InboxNeed Need(ConversationItem conversation, bool answersAgents) =>
        InboxAttentionRules.Reason(conversation, Me, [], answersAgents);

    [Fact]
    public void Reason_AnUnassignedAgentQuestion_NeedsWhoeverCanAnswerIt()
    {
        ConversationItem conversation = new() { Id = "chat", NeedsAnswer = true };

        Assert.Equal(InboxNeed.AgentAnswer, Need(conversation, answersAgents: true));
    }

    [Fact]
    public void Reason_AnUnassignedAgentQuestion_DoesNotNeedSomeoneWhoCannotAnswerIt()
    {
        ConversationItem conversation = new() { Id = "chat", NeedsAnswer = true };

        Assert.Equal(InboxNeed.None, Need(conversation, answersAgents: false));
    }

    [Fact]
    public void Reason_AnUnassignedApproval_NeedsWhoeverCanDecideIt()
    {
        ConversationItem conversation = new() { Id = "chat", PendingApproval = new("apr_1", "payments.refund", null) };

        Assert.Equal(InboxNeed.Approval, Need(conversation, answersAgents: true));
        Assert.Equal(InboxNeed.None, Need(conversation, answersAgents: false));
    }

    [Fact]
    public void Reason_AQuestionNamedForSomeoneElse_DoesNotNeedMeEvenThoughICanAnswer()
    {
        ConversationItem attention = new() { Id = "chat", NeedsAnswer = true, Attention = new InboxAttentionDto(Other) };
        ConversationItem assigned = new() { Id = "chat", NeedsAnswer = true, Assignee = new InboxPersonDto(Other, "Other") };

        Assert.Equal(InboxNeed.None, Need(attention, answersAgents: true));
        Assert.Equal(InboxNeed.None, Need(assigned, answersAgents: true));
    }

    [Fact]
    public void Reason_AQuestionNamedForMe_NeedsMeWhateverMyPermissions()
    {
        ConversationItem conversation = new() { Id = "chat", NeedsAnswer = true, Attention = new InboxAttentionDto(Me) };

        Assert.Equal(InboxNeed.AgentAnswer, Need(conversation, answersAgents: false));
    }

    [Fact]
    public void Reason_WithNothingOpen_DoesNotNeedAnyone()
    {
        ConversationItem conversation = new() { Id = "chat", UnreadCount = 4, Priority = ConversationPriority.Urgent };

        Assert.Equal(InboxNeed.None, Need(conversation, answersAgents: true));
    }

    [Fact]
    public void Label_AnAgentQuestion_SaysItNeedsYourAnswer()
    {
        Assert.Equal("Needs your answer", InboxAttentionRules.Label(InboxNeed.AgentAnswer));
    }
}
