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

        state.Touch(ada, Pushed("in", text: "New"));

        Assert.Same(ada, state.Conversations[0]);
        Assert.Equal("New", ada.LastMessage);
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

    [Fact]
    public void TheReplyWindowComesFromTheListAndRunsFromTheCustomersLastMessage()
    {
        var state = new ConversationsState();
        var wrote = DateTime.UtcNow.AddHours(-2);
        state.Apply([new InboxConversationDto(Connection, "2348012345678", "Ada", "Hi", DateTime.UtcNow)
            { LastCustomerMessageAt = wrote, ReplyWindowHours = 24 }]);

        var item = state.Conversations[0];

        Assert.Equal(wrote.AddHours(24), item.ReplyWindowEndsAt);
        Assert.False(item.ReplyWindowClosed);
    }

    [Fact]
    public void AWindowThatEndedClosesTheReply()
    {
        var item = new ConversationItem { LastCustomerMessageAt = DateTime.UtcNow.AddHours(-25), ReplyWindowHours = 24 };
        Assert.True(item.ReplyWindowClosed);
    }

    [Fact]
    public void WithoutAWindowFromTheApiNothingIsClosed()
    {
        var item = new ConversationItem { LastCustomerMessageAt = DateTime.UtcNow.AddDays(-30) };
        Assert.Null(item.ReplyWindowEndsAt);
        Assert.False(item.ReplyWindowClosed);
    }

    [Fact]
    public void ACustomersPushOpensTheWindowAgainButAReplyAndAnOlderPushDoNot()
    {
        var state = StateWith("2348012345678");
        var ada = state.Conversations[0];
        ada.ReplyWindowHours = 24;
        ada.LastCustomerMessageAt = DateTime.UtcNow.AddHours(-30);
        Assert.True(ada.ReplyWindowClosed);

        var now = DateTime.UtcNow;
        state.Touch(ada, Pushed("in", at: now) with { SentAt = now });
        Assert.Equal(now, ada.LastCustomerMessageAt);
        Assert.False(ada.ReplyWindowClosed);

        state.Touch(ada, Pushed("old", at: now.AddHours(-2)) with { SentAt = now.AddHours(-2) });
        Assert.Equal(now, ada.LastCustomerMessageAt);

        state.Touch(ada, Pushed("out", outgoing: true, at: now.AddMinutes(5)));
        Assert.Equal(now, ada.LastCustomerMessageAt);
    }

    [Theory]
    [InlineData(-5, "Reply window closed")]
    [InlineData(0, "Reply window closed")]
    [InlineData(30, "Under 1m left to reply")]
    [InlineData(45 * 60, "45m left to reply")]
    [InlineData(3 * 3600 + 12 * 60, "3h 12m left to reply")]
    [InlineData(47 * 3600 + 59 * 60, "47h 59m left to reply")]
    public void TheTimeLeftReadsAsAPersonWouldSayIt(int secondsLeft, string expected)
    {
        var now = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expected, ReplyWindowLabel.Describe(now.AddSeconds(secondsLeft), now));
    }
}
