using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Moq;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Integrations;
using Parrot.Domain.Entities.Messages;
using Parrot.Domain.Enums;

namespace Test.Parrot.Api.Tests.Services.Integrations;

public class ConnectionMessageReceiptTests
{
    [Fact]
    public async Task ReceiptIsRecordedOnlyAfterMongoWriteCompletes()
    {
        Mock<IMessageRepository> messages = new();
        Mock<IConnectionMessageReceipt> receipts = new();
        TaskCompletionSource write = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IncomingMessage message = Message();
        messages.Setup(m => m.InsertAsync(message, default)).Returns(write.Task);
        receipts.Setup(r => r.RecordAsync(message, default)).Returns(Task.CompletedTask);
        ConnectionTrackingMessageRepository repository = new(messages.Object, receipts.Object);

        Task pending = repository.InsertAsync(message);
        receipts.Verify(r => r.RecordAsync(It.IsAny<IncomingMessage>(), default), Times.Never);
        write.SetResult();
        await pending;
        receipts.Verify(r => r.RecordAsync(message, default), Times.Once);
    }

    [Fact]
    public async Task FailedMongoWriteNeverMarksReceivingConfigured()
    {
        Mock<IMessageRepository> messages = new();
        Mock<IConnectionMessageReceipt> receipts = new();
        IncomingMessage message = Message();
        messages.Setup(m => m.InsertAsync(message, default)).ThrowsAsync(new InvalidOperationException("MongoDB unavailable"));
        ConnectionTrackingMessageRepository repository = new(messages.Object, receipts.Object);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.InsertAsync(message));
        receipts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FailedReceiptUpdatePropagatesForKafkaRetry()
    {
        Mock<IMessageRepository> messages = new();
        Mock<IConnectionMessageReceipt> receipts = new();
        IncomingMessage message = Message();
        messages.Setup(m => m.InsertAsync(message, default)).Returns(Task.CompletedTask);
        receipts.Setup(r => r.RecordAsync(message, default)).ThrowsAsync(new InvalidOperationException("PostgreSQL unavailable"));
        ConnectionTrackingMessageRepository repository = new(messages.Object, receipts.Object);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.InsertAsync(message));
    }

    [Fact]
    public void MongoDocumentPreservesConnectionOwnership()
    {
        Guid id = Guid.NewGuid();
        WhatsAppMessageDocument original = new()
        {
            ConnectionId = id, OwnerUserId = "owner", MessageId = "message",
            Platform = MessagingPlatform.WhatsApp, ExternalAccountId = "business",
            From = "sender", Timestamp = "123", Type = "text", ChannelId = "phone", ChannelDisplayName = "number",
        };
        WhatsAppMessageDocument restored = BsonSerializer.Deserialize<WhatsAppMessageDocument>(original.ToBson());
        Assert.Equal(id, restored.ConnectionId);
        Assert.Equal("owner", restored.OwnerUserId);
    }

    private static IncomingMessage Message() => new()
    {
        ConnectionId = Guid.NewGuid(), OwnerUserId = "owner", MessageId = "message",
        Platform = MessagingPlatform.WhatsApp, ExternalAccountId = "business", From = "sender",
        Timestamp = "123", Type = "text", ChannelId = "phone", ChannelDisplayName = "number", ReceivedAt = DateTime.UtcNow,
    };
}
