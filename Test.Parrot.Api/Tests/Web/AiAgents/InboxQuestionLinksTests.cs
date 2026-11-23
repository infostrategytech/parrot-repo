using Parrot.Web.Components.Pages.Dashboard.AiAgents;
using Parrot.Web.Features.Dashboard.AiAgents;

namespace Test.Parrot.Api.Tests.Web.AiAgents;

// "View in Inbox" on the Agents page: one question opens its chat, several open Needs you filtered to them.
public sealed class InboxQuestionLinksTests
{
    private static WaitingQuestion Question(string? conversation, string? message = null) =>
        new("Nova", "Ada", "Do you deliver to Ibadan?") { AgentId = "agent-nova", ConversationId = conversation, MessageId = message };

    [Fact]
    public void Review_OneQuestionWithItsConversation_OpensThatConversationInNeedsYou()
    {
        string link = InboxQuestionLinks.Review([Question("3333:2348012345678")]);

        Assert.Equal("/dashboard/inbox/3333%3A2348012345678?view=needs-you&questions=true", link);
    }

    [Fact]
    public void Review_OneQuestionWithAMessage_OpensItAtThatMessage()
    {
        string link = InboxQuestionLinks.Review([Question("3333:2348012345678", "wamid.1")], "agent-nova");

        Assert.Equal("/dashboard/inbox/3333%3A2348012345678?view=needs-you&questions=true&agent=agent-nova&message=wamid.1", link);
    }

    [Fact]
    public void Review_SeveralQuestions_OpensNeedsYouFilteredToAgentQuestions()
    {
        string link = InboxQuestionLinks.Review([Question("3333:a"), Question("3333:b")]);

        Assert.Equal("/dashboard/inbox?view=needs-you&questions=true", link);
    }

    [Fact]
    public void Review_AQuestionWithoutItsConversation_OpensNeedsYou()
    {
        Assert.Equal(InboxQuestionLinks.Queue(), InboxQuestionLinks.Review([Question(null)]));
    }
}
