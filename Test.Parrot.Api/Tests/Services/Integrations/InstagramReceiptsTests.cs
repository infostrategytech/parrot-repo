using System.Text.Json;
using Microsoft.Extensions.Options;
using Moq;
using Parrot.Application.Conversations;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Integrations;
using Parrot.Application.Logging;
using Parrot.Application.Messaging;
using Parrot.Domain.Entities;

namespace Test.Parrot.Api.Tests.Services.Integrations;

public class InstagramReceiptsTests
{
    private const string AccountId = "17841400000000000";
    private const string Customer = "igsid-1";
    private const long EventTime = 1_700_000_000_000;
    private static readonly DateTime EventAt = DateTimeOffset.FromUnixTimeMilliseconds(EventTime).UtcDateTime;
    private static readonly DateTime SentAt = EventAt.AddMinutes(-5);

    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly Mock<IDeliveryReceipts> _receipts = new();
    private readonly Mock<IReplySendTimes> _sendTimes = new();
    private readonly Mock<IReceiptNotifier> _notifier = new();

    private Task<bool> ProcessAsync(string body, IDeliveryReceipts? receipts)
    {
        Mock<IInstagramConnectionRepository> connections = new();
        connections.Setup(c => c.GetActiveByIdentifierAsync("ref", It.IsAny<CancellationToken>())).ReturnsAsync(new InstagramConnection
        {
            Id = _connectionId, WorkspaceId = "owner-1", VerifyTokenEncrypted = "enc", AccountId = AccountId, WebhookVerifiedAt = DateTime.UtcNow,
        });
        InstagramWebhookService service = new(
            Options.Create(new InstagramSettings()),
            connections.Object,
            new InboundMessagePublisher(Mock.Of<IMessageBus>(), Options.Create(new KafkaSettings()), Mock.Of<IAppLogger<InboundMessagePublisher>>()),
            Mock.Of<IAppLogger<InstagramWebhookService>>(),
            Mock.Of<IInstagramCredentialProtector>(),
            receipts, _sendTimes.Object, _notifier.Object);
        return service.ProcessConnectionEventAsync("ref", JsonSerializer.Deserialize<FacebookWebhookPayload>(body)!, body);
    }

    private static string Read(string read) => "{\"object\":\"instagram\",\"entry\":[{\"id\":\"" + AccountId + "\",\"time\":1,\"messaging\":[{"
        + "\"sender\":{\"id\":\"" + Customer + "\"},\"recipient\":{\"id\":\"" + AccountId + "\"},\"timestamp\":" + EventTime + ",\"read\":" + read + "}]}]}";

    [Fact]
    public async Task AReadMarksThatReplyAndEveryEarlierOneRead()
    {
        _sendTimes.Setup(s => s.SentAtAsync("mid.1", It.IsAny<CancellationToken>())).ReturnsAsync(SentAt);

        Assert.True(await ProcessAsync(Read("{\"mid\":\"mid.1\"}"), _receipts.Object));

        _receipts.Verify(r => r.RecordAsync(_connectionId, "mid.1", DeliveryState.Read, EventAt, It.IsAny<CancellationToken>()), Times.Once);
        _receipts.Verify(r => r.RecordUpToAsync(_connectionId, Customer, DeliveryState.Read, EventAt, SentAt, It.IsAny<CancellationToken>()), Times.Once);
        _notifier.Verify(n => n.ReceiptsChangedAsync("owner-1", _connectionId, Customer, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AReadOfAnUnknownReplyStillMarksTheMessageItNames()
    {
        _sendTimes.Setup(s => s.SentAtAsync("mid.1", It.IsAny<CancellationToken>())).ReturnsAsync((DateTime?)null);

        await ProcessAsync(Read("{\"mid\":\"mid.1\"}"), _receipts.Object);

        _receipts.Verify(r => r.RecordAsync(_connectionId, "mid.1", DeliveryState.Read, EventAt, It.IsAny<CancellationToken>()), Times.Once);
        _receipts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AReadWithoutAMessageIdIsIgnored()
    {
        Assert.True(await ProcessAsync(Read("{}"), _receipts.Object));

        _receipts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WithoutReceiptsConfiguredAReadIsStillAccepted() =>
        Assert.True(await ProcessAsync(Read("{\"mid\":\"mid.1\"}"), null));

    [Fact]
    public async Task AFailureRecordingAReadNeverFailsTheWebhook()
    {
        _receipts.Setup(r => r.RecordAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DeliveryState>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("mongo down"));

        Assert.True(await ProcessAsync(Read("{\"mid\":\"mid.1\"}"), _receipts.Object));
    }
}
