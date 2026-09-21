using Parrot.Application.Conversations;
using Parrot.Web.Components.Pages.Dashboard.Conversations;

namespace Test.Parrot.Api.Tests.Services;

public class ConversationStateTests
{
    [Fact]
    public void PollingAndSendResponsesMergeByIdAndUpdateStatus()
    {
        var item = new ConversationItem();
        var now = DateTime.UtcNow;
        var initial = new ConversationMessage("one", "Hello", true, "pending", now);
        ConversationsState.Merge(item, new[] { initial, initial });
        ConversationsState.Merge(item, new[] { initial with { Status = "accepted" } });
        Assert.Single(item.Messages);
        Assert.Equal("accepted", item.Messages[0].Status);
        Assert.True(item.Messages[0].IsOutgoing);
    }

    [Fact]
    public void RepeatedConversationPagesDoNotDuplicateRows()
    {
        var state = new ConversationsState();
        var summary = new ConversationSummary(Guid.NewGuid(), "123", "Name", "Hi", DateTime.UtcNow);
        state.Apply(new[] { summary, summary });
        state.Apply(new[] { summary });
        Assert.Single(state.Conversations);
    }
}
