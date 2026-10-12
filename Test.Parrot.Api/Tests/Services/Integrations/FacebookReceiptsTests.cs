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

public class FacebookReceiptsTests
{
    private const string PageId = "5647382910";
    private const string Customer = "1029384756";
    private const long Watermark = 1790589299000;
    private const long EventTime = 1790589300000;
    private static readonly DateTime WatermarkTime = DateTimeOffset.FromUnixTimeMilliseconds(Watermark).UtcDateTime;
    private static readonly DateTime EventAt = DateTimeOffset.FromUnixTimeMilliseconds(EventTime).UtcDateTime;

    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly Mock<IDeliveryReceipts> _receipts = new();
    private readonly Mock<IMessageBus> _bus = new();
    private readonly Mock<IReceiptNotifier> _notifier = new();

    private FacebookWebhookService Webhooks(IDeliveryReceipts? receipts, IReceiptNotifier? notifier = null)
    {
        Mock<IFacebookConnectionRepository> connections = new();
        connections.Setup(c => c.GetActiveByIdentifierAsync("reference", It.IsAny<CancellationToken>())).ReturnsAsync(new FacebookConnection
        {
            Id = _connectionId, OwnerUserId = "owner-1", VerifyTokenEncrypted = "encrypted", PageId = PageId,
            PageName = "Oma's Kitchen", WebhookVerifiedAt = DateTime.UtcNow,
        });
        return new FacebookWebhookService(
            Options.Create(new FacebookSettings { AppSecret = "test-secret", WebhookBaseUrl = "https://api.example.com" }),
            connections.Object,
            new InboundMessagePublisher(_bus.Object, Options.Create(new KafkaSettings()), Mock.Of<IAppLogger<InboundMessagePublisher>>()),
            Mock.Of<IAppLogger<FacebookWebhookService>>(),
            Mock.Of<IFacebookCredentialProtector>(),
            receipts, notifier);
    }

    private static string Event(string kind) => "{\"object\":\"page\",\"entry\":[{\"id\":\"" + PageId + "\",\"time\":1,\"messaging\":[{"
        + "\"sender\":{\"id\":\"" + Customer + "\"},\"recipient\":{\"id\":\"" + PageId + "\"},\"timestamp\":" + EventTime + "," + kind + "}]}]}";

    private Task<bool> ProcessAsync(string body, IDeliveryReceipts? receipts, IReceiptNotifier? notifier = null) =>
        Webhooks(receipts, notifier).ProcessConnectionEventAsync("reference", JsonSerializer.Deserialize<FacebookWebhookPayload>(body)!, body);

    [Fact]
    public async Task ADeliveryNamesItsMessagesAndCoversEverythingBeforeItsWatermark()
    {
        string body = Event("\"delivery\":{\"mids\":[\"m_1\",\"m_2\"],\"watermark\":" + Watermark + "}");

        Assert.True(await ProcessAsync(body, _receipts.Object));

        _receipts.Verify(r => r.RecordAsync(_connectionId, "m_1", DeliveryState.Delivered, EventAt, It.IsAny<CancellationToken>()), Times.Once);
        _receipts.Verify(r => r.RecordAsync(_connectionId, "m_2", DeliveryState.Delivered, EventAt, It.IsAny<CancellationToken>()), Times.Once);
        _receipts.Verify(r => r.RecordUpToAsync(_connectionId, Customer, DeliveryState.Delivered, EventAt, WatermarkTime, It.IsAny<CancellationToken>()), Times.Once);
        _receipts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ADeliveryWithoutAnEventTimeIsStampedWithItsWatermark()
    {
        string body = "{\"object\":\"page\",\"entry\":[{\"id\":\"" + PageId + "\",\"time\":1,\"messaging\":[{\"sender\":{\"id\":\"" + Customer
            + "\"},\"recipient\":{\"id\":\"" + PageId + "\"},\"delivery\":{\"mids\":[\"m_1\"],\"watermark\":" + Watermark + "}}]}]}";

        await ProcessAsync(body, _receipts.Object);

        _receipts.Verify(r => r.RecordAsync(_connectionId, "m_1", DeliveryState.Delivered, WatermarkTime, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AReadCoversEveryReplyToThatCustomerBeforeItsWatermark()
    {
        Assert.True(await ProcessAsync(Event("\"read\":{\"watermark\":" + Watermark + "}"), _receipts.Object));

        _receipts.Verify(r => r.RecordUpToAsync(_connectionId, Customer, DeliveryState.Read, EventAt, WatermarkTime, It.IsAny<CancellationToken>()), Times.Once);
        _receipts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AReadWithoutAWatermarkRecordsNothing()
    {
        // Instagram's seen event names a message, not a watermark.
        Assert.True(await ProcessAsync(Event("\"read\":{\"mid\":\"m_9\"}"), _receipts.Object));

        _receipts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReceiptsAreNotMessages()
    {
        await ProcessAsync(Event("\"read\":{\"watermark\":" + Watermark + "}"), _receipts.Object);

        _bus.Verify(b => b.PublishAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ARecorderThatFailsOrHangsNeverFailsOrDelaysTheWebhook()
    {
        Mock<IDeliveryReceipts> failing = new();
        failing.Setup(r => r.RecordUpToAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DeliveryState>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database down"));
        Assert.True(await ProcessAsync(Event("\"read\":{\"watermark\":" + Watermark + "}"), failing.Object));

        Mock<IDeliveryReceipts> hanging = new();
        hanging.Setup(r => r.RecordUpToAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DeliveryState>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, DeliveryState, DateTime, DateTime, CancellationToken>(async (_, _, _, _, _, ct) => { await Task.Delay(Timeout.Infinite, ct); });
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(await ProcessAsync(Event("\"read\":{\"watermark\":" + Watermark + "}"), hanging.Object));
        Assert.InRange(clock.Elapsed.TotalSeconds, 0, 3);
    }

    [Fact]
    public async Task WithoutARecorderTheWebhookStillWorks() =>
        Assert.True(await ProcessAsync(Event("\"read\":{\"watermark\":" + Watermark + "}"), receipts: null));

    [Fact]
    public async Task TheOwnersInboxIsToldWhichCustomersThreadChanged()
    {
        await ProcessAsync(Event("\"read\":{\"watermark\":" + Watermark + "}"), _receipts.Object, _notifier.Object);

        _notifier.Verify(n => n.ReceiptsChangedAsync("owner-1", _connectionId, Customer, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ANotifierThatFailsNeverFailsTheWebhook()
    {
        Mock<IReceiptNotifier> failing = new();
        failing.Setup(n => n.ReceiptsChangedAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("redis down"));

        Assert.True(await ProcessAsync(Event("\"read\":{\"watermark\":" + Watermark + "}"), _receipts.Object, failing.Object));
    }
}
