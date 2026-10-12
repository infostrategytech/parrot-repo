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

// The webhook records on the static message meter, so it runs with the other tests that read it.
[Collection("MessageMetricsCollection")]
public class WhatsAppReceiptsTests
{
    private static string Body(string statuses) =>
        """
        {"object":"whatsapp_business_account","entry":[{"id":"waba-1","changes":[{"field":"messages","value":{
          "messaging_product":"whatsapp",
          "metadata":{"display_phone_number":"+234 901 111 2222","phone_number_id":"phone-1"},
          "statuses":[
        """ + statuses + "]}}]}]}";

    private static string Status(string id, string status, string timestamp = "1790589124") =>
        "{\"id\":\"" + id + "\",\"status\":\"" + status + "\",\"timestamp\":\"" + timestamp + "\",\"recipient_id\":\"2348012345678\"}";

    private readonly WhatsAppConnection _connection = new()
    {
        OwnerUserId = "owner-1", Name = "Shop", VerifyTokenEncrypted = "x", WhatsAppBusinessAccountId = "waba-1",
        PhoneNumberId = "phone-1", WebhookVerifiedAt = DateTime.UtcNow,
    };
    private readonly Mock<IDeliveryReceipts> _receipts = new();

    private WhatsAppWebhookService Webhooks(IDeliveryReceipts? receipts)
    {
        Mock<IWhatsAppConnectionRepository> connections = new();
        connections.Setup(c => c.GetActiveByIdentifierAsync(It.IsAny<string>())).ReturnsAsync(_connection);
        return new WhatsAppWebhookService(
            Options.Create(new WhatsAppSettings()),
            Mock.Of<IAppLogger<WhatsAppWebhookService>>(),
            new InboundMessagePublisher(Mock.Of<IMessageBus>(), Options.Create(new KafkaSettings()), Mock.Of<IAppLogger<InboundMessagePublisher>>()),
            connections.Object,
            Mock.Of<IWhatsAppTokenProtector>(),
            receipts: receipts);
    }

    private Task<bool> ProcessAsync(string body, IDeliveryReceipts? receipts) =>
        Webhooks(receipts).ProcessConnectionEventAsync("ref", JsonSerializer.Deserialize<WhatsAppWebhookPayload>(body)!, body);

    [Fact]
    public async Task DeliveredReadAndFailedAreRecordedOnThisConnection_SentIsNot()
    {
        string body = Body(string.Join(",",
            Status("wamid.1", "sent"), Status("wamid.1", "delivered", "1790589200"),
            Status("wamid.1", "read", "1790589300"), Status("wamid.2", "failed")));

        Assert.True(await ProcessAsync(body, _receipts.Object));

        _receipts.Verify(r => r.RecordAsync(_connection.Id, "wamid.1", DeliveryState.Delivered,
            DateTimeOffset.FromUnixTimeSeconds(1790589200).UtcDateTime, It.IsAny<CancellationToken>()), Times.Once);
        _receipts.Verify(r => r.RecordAsync(_connection.Id, "wamid.1", DeliveryState.Read,
            DateTimeOffset.FromUnixTimeSeconds(1790589300).UtcDateTime, It.IsAny<CancellationToken>()), Times.Once);
        _receipts.Verify(r => r.RecordAsync(_connection.Id, "wamid.2", DeliveryState.Failed, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        _receipts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ABadTimestampStillRecordsTheReceiptAtTheTimeItArrived()
    {
        DateTime before = DateTime.UtcNow;

        await ProcessAsync(Body(Status("wamid.1", "read", "not-a-number")), _receipts.Object);

        _receipts.Verify(r => r.RecordAsync(_connection.Id, "wamid.1", DeliveryState.Read,
            It.Is<DateTime>(at => at >= before), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ARecorderThatFailsOrHangsNeverFailsOrDelaysTheWebhook()
    {
        Mock<IDeliveryReceipts> failing = new();
        failing.Setup(r => r.RecordAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DeliveryState>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database down"));
        Assert.True(await ProcessAsync(Body(Status("wamid.1", "read")), failing.Object));

        Mock<IDeliveryReceipts> hanging = new();
        hanging.Setup(r => r.RecordAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DeliveryState>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, DeliveryState, DateTime, CancellationToken>(async (_, _, _, _, ct) => { await Task.Delay(Timeout.Infinite, ct); });
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(await ProcessAsync(Body(Status("wamid.1", "read")), hanging.Object));
        Assert.InRange(clock.Elapsed.TotalSeconds, 0, 3);
    }

    [Fact]
    public async Task WithoutARecorderTheWebhookStillWorks() =>
        Assert.True(await ProcessAsync(Body(Status("wamid.1", "read")), receipts: null));
}
