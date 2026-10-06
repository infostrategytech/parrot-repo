using Parrot.Application.Messaging;
using Parrot.Domain.Enums;
using Parrot.Web.Services;
using Parrot.Web.Components.Pages.Dashboard.Conversations;

namespace Test.Parrot.Api.Tests.Services;

public class ConversationStateTests
{
    [Fact]
    public void PollingAndSendResponsesMergeByIdAndUpdateStatus()
    {
        var item = new ConversationItem();
        var now = DateTime.UtcNow;
        var initial = new InboxMessageDto("one", "Hello", true, "pending", now);
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
        var summary = new InboxConversationDto(Guid.NewGuid(), "123", "Name", "Hi", DateTime.UtcNow);
        state.Apply(new[] { summary, summary });
        state.Apply(new[] { summary });
        Assert.Single(state.Conversations);
    }
    private static readonly Guid Connection = Guid.NewGuid();

    private static StoredMessage Pushed(string id, bool outgoing = false, string? text = "Hi", DateTime? at = null) => new()
    {
        MessageId = id, ConversationKey = "whatsapp:2348012345678:2349011112222", Sequence = 1,
        Platform = MessagingPlatform.WhatsApp, ConnectionId = Connection, Outgoing = outgoing,
        Sender = outgoing ? "2349011112222" : "2348012345678",
        Receiver = outgoing ? "2348012345678" : "2349011112222",
        Type = "text", Text = text, PersistedAt = at ?? DateTime.UtcNow,
    };

    private static ConversationsState StateWith(params string[] senders)
    {
        var state = new ConversationsState();
        state.Apply(senders.Select(s => new InboxConversationDto(Connection, s, s, "", DateTime.UtcNow.AddHours(-1))));
        return state;
    }

    [Fact]
    public void APushFindsItsConversation_ByTheCustomer_InBothDirections()
    {
        var state = StateWith("2348012345678", "2348033334444");

        Assert.Equal("2348012345678", state.FindForPush(Pushed("in"))!.Sender);
        Assert.Equal("2348012345678", state.FindForPush(Pushed("out", outgoing: true))!.Sender);
    }

    [Fact]
    public void APushForAnUnlistedConversation_FindsNothing()
    {
        var state = StateWith("2348033334444");

        Assert.Null(state.FindForPush(Pushed("in")));
        Assert.Null(state.FindForPush(Pushed("in") with { ConnectionId = Guid.NewGuid() }));
    }

    [Fact]
    public void APushedCustomerMessage_JoinsTheThreadOnce_AsAReadWouldShowIt()
    {
        var item = new ConversationItem();

        Assert.True(ConversationsState.MergePushed(item, Pushed("in", text: null) with { Type = "image" }));
        Assert.False(ConversationsState.MergePushed(item, Pushed("in")));

        var message = Assert.Single(item.Messages);
        Assert.Equal("[image message]", message.Text);
        Assert.Equal("received", message.Status);
        Assert.Equal(MessageKind.Customer, message.Kind);
    }

    // The read's copy carries the API's exact time; a late push must not replace it.
    [Fact]
    public void ARepeatedPush_LeavesTheReadsCopy()
    {
        var item = new ConversationItem();
        var read = DateTime.UtcNow.AddMinutes(-1);
        ConversationsState.Merge(item, [new InboxMessageDto("in", "Hi", false, "received", read)]);

        ConversationsState.MergePushed(item, Pushed("in"));

        Assert.Equal(read, Assert.Single(item.Messages).ReceivedAt);
    }

    [Fact]
    public void APushedReply_IsNotMergedWithoutItsSendStatus()
    {
        Assert.Throws<ArgumentException>(() => ConversationsState.MergePushed(new ConversationItem(), Pushed("out", outgoing: true)));
    }

    [Fact]
    public void APush_MovesItsConversationToTheTop_WithItsPreview()
    {
        var state = StateWith("2348033334444", "2348012345678");
        var ada = state.FindForPush(Pushed("in"))!;
        int changes = 0;
        state.Changed += () => changes++;

        state.Touch(ada, Pushed("in", text: "New"));

        Assert.Same(ada, state.Conversations[0]);
        Assert.Equal("New", ada.LastMessage);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void AnOlderPush_DoesNotReplaceANewerPreview()
    {
        var state = StateWith("2348012345678");
        var ada = state.Conversations[0];
        ada.LastMessage = "Latest";

        state.Touch(ada, Pushed("old", text: "Old", at: DateTime.UtcNow.AddHours(-2)));

        Assert.Equal("Latest", ada.LastMessage);
    }
}
