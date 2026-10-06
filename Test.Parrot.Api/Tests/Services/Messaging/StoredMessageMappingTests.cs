using Parrot.Application.Messaging;
using Parrot.Domain.Entities.Messages;
using Parrot.Domain.Enums;

namespace Test.Parrot.Api.Tests.Services.Messaging;

// From is always the customer and Receiver always the business, so the shared
// shape must swap them for a reply.
public class StoredMessageMappingTests
{
    [Theory]
    [InlineData(false, "2348012345678", "2349011112222")]
    [InlineData(true, "2349011112222", "2348012345678")]
    public void SenderAndReceiverFollowTheDirection(bool outgoing, string sender, string receiver)
    {
        StoredMessage message = new WhatsAppMessageDocument
        {
            MessageId = "whatsapp:wamid.1", Platform = MessagingPlatform.WhatsApp, ExternalAccountId = "456",
            ChannelId = "123", ChannelDisplayName = "+234 901 111 2222", From = "2348012345678", Timestamp = "0",
            Type = "text", Outgoing = outgoing, Receiver = "2349011112222",
            ConversationKey = "whatsapp:2348012345678:2349011112222", Sequence = 1,
        }.ToStoredMessage();

        Assert.Equal(sender, message.Sender);
        Assert.Equal(receiver, message.Receiver);
    }
}
