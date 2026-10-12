using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Parrot.Application.Conversations;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Integrations;
using Parrot.Application.Logging;
using Parrot.Application.Messaging;
using Parrot.Domain.Entities;
using Parrot.Domain.Entities.Messages;
using Parrot.Infrastructure.Conversations;
using Parrot.Infrastructure.Messaging;

namespace Test.Parrot.Api.Tests.Monitoring;

// MessageMetrics is one static meter, so these run alone: other tests recording
// on it in parallel would leak into the counts asserted here.
[CollectionDefinition(nameof(MessageMetricsCollection), DisableParallelization = true)]
public sealed class MessageMetricsCollection;

[Collection(nameof(MessageMetricsCollection))]
public class MessageMetricsTests : IDisposable
{
    private const string Body = """
        {"object":"whatsapp_business_account","entry":[{"id":"waba-1","changes":[{"field":"messages","value":{
          "messaging_product":"whatsapp",
          "metadata":{"display_phone_number":"+234 901 111 2222","phone_number_id":"phone-1"},
          "contacts":[{"wa_id":"2348012345678","profile":{"name":"Ada"}}],
          "messages":[{"from":"2348012345678","id":"wamid.A","timestamp":"1790589123","type":"text","text":{"body":"Hello"}}],
          "statuses":[{"id":"wamid.reply","status":"delivered","timestamp":"1790589124","recipient_id":"2348012345678"}]}}]}]}
        """;

    private readonly MeterListener _listener = new();
    private readonly List<(string Name, double Value, Dictionary<string, object?> Tags)> _measurements = [];

    public MessageMetricsTests()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == MessageMetrics.MeterName) listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.Start();
    }

    public void Dispose() => _listener.Dispose();

    private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        Dictionary<string, object?> copy = [];
        foreach (KeyValuePair<string, object?> tag in tags) copy[tag.Key] = tag.Value;
        lock (_measurements) _measurements.Add((instrument.Name, value, copy));
    }

    private double Sum(string name, string? tag = null, string? value = null) =>
        _measurements.Where(m => m.Name == name && (tag is null || Equals(m.Tags.GetValueOrDefault(tag), value))).Sum(m => m.Value);

    private int Samples(string name, string? tag = null, string? value = null) =>
        _measurements.Count(m => m.Name == name && (tag is null || Equals(m.Tags.GetValueOrDefault(tag), value)));

    // ---- stages 2-4: the processor ------------------------------------------

    private readonly List<MessageDocument> _stored = [];

    private InboundMessageProcessor Processor(Exception? storeFailure = null)
    {
        Mock<IStoredMessageStore> store = new();
        store.Setup(s => s.FindAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((id, _) => storeFailure is null
                ? Task.FromResult(_stored.FirstOrDefault(d => d.MessageId == id))
                : Task.FromException<MessageDocument?>(storeFailure));
        store.Setup(s => s.InsertOrGetAsync(It.IsAny<MessageDocument>(), It.IsAny<CancellationToken>()))
            .Returns<MessageDocument, CancellationToken>((doc, _) => { _stored.Add(doc); return Task.FromResult(doc); });
        Mock<IConversationSequence> sequence = new();
        sequence.Setup(s => s.NextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(1);

        return new InboundMessageProcessor(
            new InboundMessageDecoders([new WhatsAppInboundDecoder(), new FacebookInboundDecoder()]),
            store.Object,
            sequence.Object,
            new MessageDocumentMapperFactory([new WhatsAppMessageDocumentMapper(), new FacebookMessageDocumentMapper(), new TikTokMessageDocumentMapper(), new InstagramMessageDocumentMapper()]),
            Mock.Of<IConnectionMessageReceipt>(),
            Mock.Of<IMessagePersistedPublisher>(),
            NullLogger<InboundMessageProcessor>.Instance);
    }

    private static InboundMessage Inbound(string body = Body) => new()
    {
        MessageId = "whatsapp:wamid.A",
        Source = global::Parrot.Domain.Enums.MessagingPlatform.WhatsApp,
        Sender = "2348012345678",
        Receiver = "2349011112222",
        ConversationKey = "whatsapp:2348012345678:2349011112222",
        Data = body,
        ReceivedAt = DateTime.UtcNow.AddSeconds(-2),
        CorrelationId = "487b7279745d4c41675818ffee460c54",
    };

    [Fact]
    public async Task A_new_message_is_persisted_processed_and_timed()
    {
        await Processor().ProcessAsync(Inbound(), CancellationToken.None);

        Assert.Equal(1, Sum("messages_persisted_total", "message_type", "text"));
        Assert.Equal(0, Sum("messages_deduplicated_total"));
        Assert.Equal(1, Sum("messages_processed_total", "status", "success"));
        (string _, double wait, Dictionary<string, object?> waitTags) = Assert.Single(_measurements, m => m.Name == "message_queue_wait_seconds");
        Assert.InRange(wait, 1, 60);
        Assert.Equal("messages.inbound", waitTags["queue"]);
        Assert.Equal(["decode", "persist", "publish"],
            _measurements.Where(m => m.Name == "message_processing_duration_seconds").Select(m => (string)m.Tags["stage"]!));
    }

    // received = persisted + deduplicated + ...: a resent message must not count as persisted again.
    [Fact]
    public async Task A_message_already_stored_is_deduplicated_not_persisted()
    {
        await Processor().ProcessAsync(Inbound() with { ReceivedAt = DateTime.UtcNow.AddSeconds(-2) }, CancellationToken.None);
        await Processor().ProcessAsync(Inbound() with { ReceivedAt = DateTime.UtcNow }, CancellationToken.None);

        Assert.Equal(1, Sum("messages_persisted_total"));
        Assert.Equal(1, Sum("messages_deduplicated_total"));
    }

    // A retry or a rewound batch reads the same Kafka record again: it was counted as
    // persisted the first time, so it must not also count as a duplicate.
    [Fact]
    public async Task A_replay_of_the_same_record_is_not_counted_as_a_duplicate()
    {
        InboundMessage record = Inbound();
        await Processor().ProcessAsync(record, CancellationToken.None);
        await Processor().ProcessAsync(record, CancellationToken.None);

        Assert.Equal(1, Sum("messages_persisted_total"));
        Assert.Equal(0, Sum("messages_deduplicated_total"));
    }

    [Fact]
    public async Task A_failed_save_counts_its_reason_and_the_original_error_still_reaches_the_bus()
    {
        await Assert.ThrowsAsync<TimeoutException>(
            () => Processor(new TimeoutException("MongoDB is down")).ProcessAsync(Inbound(), CancellationToken.None));

        Assert.Equal(1, Sum("messages_persist_failed_total", "reason", "db_timeout"));
        Assert.Equal(1, Sum("messages_processed_total", "status", "failed"));
        Assert.Equal("db_timeout", _measurements.Single(m => m.Name == "messages_processed_total").Tags["reason"]);
        Assert.Equal(0, Sum("messages_persisted_total"));
    }

    [Fact]
    public async Task An_unprocessable_message_counts_as_dead_lettered_with_its_reason()
    {
        await Assert.ThrowsAsync<UnprocessableMessageException>(
            () => Processor().ProcessAsync(Inbound("not json"), CancellationToken.None));

        (string _, double _, Dictionary<string, object?> tags) = Assert.Single(_measurements, m => m.Name == "messages_processed_total");
        Assert.Equal("dead_lettered", tags["status"]);
        Assert.Equal("body_not_json", tags["reason"]);
        Assert.Equal(0, Sum("messages_persist_failed_total"));
    }

    // Payload text is untrusted: only Meta's known types become label values.
    [Theory]
    [InlineData("text", "text")]
    [InlineData("image", "image")]
    [InlineData("anything-a-caller-invents", "other")]
    [InlineData(null, "other")]
    public void Message_types_are_a_fixed_set(string? type, string label) =>
        Assert.Equal(label, MessageMetrics.MessageType(type).Value);

    [Theory]
    [InlineData(typeof(TimeoutException), "db_timeout")]
    [InlineData(typeof(InvalidOperationException), "other")]
    public void Save_failures_map_to_a_fixed_reason(Type exception, string reason) =>
        Assert.Equal(reason, MessageMetrics.Classify((Exception)Activator.CreateInstance(exception)!));

    // ---- stages 1 and 6: the webhook ----------------------------------------

    private static WhatsAppWebhookService Webhooks(WhatsAppConnection? connection, IReplySendTimes? sendTimes = null)
    {
        Mock<IWhatsAppConnectionRepository> connections = new();
        connections.Setup(c => c.GetActiveByIdentifierAsync(It.IsAny<string>())).ReturnsAsync(connection);
        return new WhatsAppWebhookService(
            Options.Create(new WhatsAppSettings()),
            Mock.Of<IAppLogger<WhatsAppWebhookService>>(),
            new InboundMessagePublisher(Mock.Of<IMessageBus>(), Options.Create(new KafkaSettings()), Mock.Of<IAppLogger<InboundMessagePublisher>>()),
            connections.Object,
            Mock.Of<IWhatsAppTokenProtector>(),
            sendTimes);
    }

    private static WhatsAppConnection Connection(string waba = "waba-1") => new()
    {
        OwnerUserId = "owner-1",
        Name = "Shop",
        VerifyTokenEncrypted = "x",
        WhatsAppBusinessAccountId = waba,
        PhoneNumberId = "phone-1",
        WebhookVerifiedAt = DateTime.UtcNow,
    };

    private static WhatsAppWebhookPayload Payload() => JsonSerializer.Deserialize<WhatsAppWebhookPayload>(Body)!;

    [Fact]
    public async Task An_accepted_webhook_counts_ok_its_messages_and_its_statuses()
    {
        Mock<IReplySendTimes> sendTimes = new();
        sendTimes.Setup(s => s.SentAtAsync("wamid.reply", It.IsAny<CancellationToken>())).ReturnsAsync(DateTime.UtcNow.AddSeconds(-5));

        Assert.True(await Webhooks(Connection(), sendTimes.Object).ProcessConnectionEventAsync("ref", Payload(), Body));

        Assert.Equal(1, Sum("whatsapp_webhooks_total", "result", "ok"));
        Assert.Equal(1, Sum("messages_received_total", "message_type", "text"));
        Assert.Equal(1, Sum("message_status_updates_total", "status", "delivered"));
        (string _, double latency, Dictionary<string, object?> _) = Assert.Single(_measurements, m => m.Name == "message_delivery_latency_seconds");
        Assert.InRange(latency, 4, 60);
    }

    // The lookup reads the database; a database that never answers must not hold up the 200 Meta waits for.
    [Fact]
    public async Task A_hanging_send_time_lookup_does_not_delay_the_webhook()
    {
        Mock<IReplySendTimes> sendTimes = new();
        sendTimes.Setup(s => s.SentAtAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return null; });

        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(await Webhooks(Connection(), sendTimes.Object).ProcessConnectionEventAsync("ref", Payload(), Body));

        Assert.InRange(clock.Elapsed.TotalSeconds, 0, 3);
        Assert.Equal(1, Sum("message_status_updates_total", "status", "delivered"));
        Assert.Equal(0, Samples("message_delivery_latency_seconds"));
    }

    [Theory]
    [InlineData(null, "unknown_connection")]
    [InlineData("waba-other", "account_mismatch")]
    public async Task A_rejected_webhook_counts_why_and_receives_nothing(string? waba, string result)
    {
        Assert.False(await Webhooks(waba is null ? null : Connection(waba)).ProcessConnectionEventAsync("ref", Payload(), Body));

        Assert.Equal(1, Sum("whatsapp_webhooks_total", "result", result));
        Assert.Equal(0, Sum("messages_received_total"));
        Assert.Equal(0, Samples("message_status_updates_total"));
    }

    // ---- stage 5: replies to the Meta API -------------------------------------

    [Fact]
    public async Task A_rejected_send_carries_metas_error_code_and_the_call_is_timed()
    {
        using HttpClient http = new(new StubHandler(HttpStatusCode.BadRequest, """{"error":{"code":131047,"message":"Re-engagement message"}}"""));
        WhatsAppSender sender = new(http, Options.Create(new WhatsAppSettings { ApiVersion = "v25.0" }));

        WhatsAppRejectedException ex = await Assert.ThrowsAsync<WhatsAppRejectedException>(
            () => sender.SendAsync("123", "secret", "2348012345678", "Hello", default));

        Assert.Equal("131047", ex.MetaErrorCode);
        Assert.Equal(1, Samples("meta_api_request_duration_seconds", "endpoint", "messages"));
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
