using Parrot.Application.Conversations;
using Parrot.Application.Messaging;
using Parrot.Domain.Enums;
using Parrot.Web.Components.Pages.Dashboard.Conversations;
using Parrot.Web.Services;

namespace Test.Parrot.Api.Tests.Services;

// The inbox's side of quoted replies: what a thread shows above a reply, and which messages can be quoted.
public class ReplyQuoteStateTests
{
    [Fact]
    public void AReadMessagesQuote_IsKeptOnTheThread()
    {
        ConversationItem item = new();

        ConversationsState.Merge(item, [new InboxMessageDto("m2", "Yes, we do", true, "read", DateTime.UtcNow)
        {
            ReplyTo = new InboxQuoteDto("m1", false, "Do you deliver?", "text"),
        }]);

        Assert.Equal(new InboxQuoteDto("m1", false, "Do you deliver?", "text"), item.Messages.Single().ReplyTo);
    }

    [Fact]
    public void APushedCustomerReply_ShowsItsQuoteWithoutARead()
    {
        ConversationItem item = new();
        StoredMessage pushed = new()
        {
            MessageId = "whatsapp:wamid.B", ConversationKey = "k", Sequence = 2, Platform = MessagingPlatform.WhatsApp, Outgoing = false,
            Sender = "2348012345678", Receiver = "2349011112222", Type = "text", Text = "That one please", PersistedAt = DateTime.UtcNow,
            ReplyTo = new StoredQuote("outgoing:u:1", true, "Jollof or fried rice?", "text"),
        };

        ConversationsState.MergePushed(item, pushed);

        Assert.Equal(new InboxQuoteDto("outgoing:u:1", true, "Jollof or fried rice?", "text"), item.Messages.Single().ReplyTo);
    }

    [Theory]
    [InlineData(MessageKind.Customer, "received", true)]
    [InlineData(MessageKind.Human, "accepted", true)]
    [InlineData(MessageKind.Human, "read", true)]
    [InlineData(MessageKind.Human, "failed", false)]
    [InlineData(MessageKind.Human, "pending", false)]
    [InlineData(MessageKind.Note, "received", false)]
    [InlineData(MessageKind.System, "received", false)]
    public void OnlyMessagesTheChannelKnows_CanBeQuoted(MessageKind kind, string status, bool quotable) =>
        Assert.Equal(quotable, QuoteLabels.CanQuote(new ConvMessage { Kind = kind, Status = status }));

    [Theory]
    [InlineData(null, "image", "Photo")]
    [InlineData("", "voice", "Voice message")]
    [InlineData(null, "document", "Document")]
    [InlineData("Hello", "text", "Hello")]
    public void AQuoteWithoutText_SaysWhatItWas(string? text, string type, string shown) =>
        Assert.Equal(shown, QuoteLabels.Text(new InboxQuoteDto("m", false, text, type)));

    [Fact]
    public void OurOwnMessages_AreQuotedAsYou() =>
        Assert.Equal("You", QuoteLabels.Author(new InboxQuoteDto("m", true, "x", "text"), "Ada"));
}
