using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Parrot.Catalog.Application.Events;

namespace Parrot.Catalog.Infrastructure.Events;

/// <summary>Publishes envelopes to Kafka, keyed by tenant and aggregate so one product's events stay ordered.</summary>
public sealed class KafkaEventBusPublisher : IEventBusPublisher, IDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly string _topic;
    private readonly string _itemsTopic;

    public KafkaEventBusPublisher(IOptions<CatalogOptions> options)
    {
        CatalogRelayOptions relay = options.Value.Relay;
        _topic = relay.Topic;
        _itemsTopic = relay.ItemsTopic;
        _producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = relay.BootstrapServers,
            Acks = Acks.All,
            EnableIdempotence = true,
            MessageSendMaxRetries = 5,
        }).Build();
    }

    /// <summary>
    /// Row events go to catalog.events as they always have (tenant_id = workspace). Whole-item
    /// events go to catalog.items as an <see cref="ItemEventMessage"/> (tenant_id = owner).
    /// Both are keyed by workspace and aggregate, so one product's events stay ordered.
    /// </summary>
    public async Task PublishAsync(EventEnvelope envelope, CancellationToken ct)
    {
        bool item = CatalogEventTypes.IsItemEvent(envelope.Type);
        string? tenant = item ? envelope.OwnerId : envelope.TenantId.ToString("D");
        Message<string, string> message = new Message<string, string>
        {
            Key = $"{envelope.TenantId:N}:{envelope.AggregateId:N}",
            Value = item
                ? JsonSerializer.Serialize(ItemEventMessage.From(envelope), EventEnvelope.WireOptions)
                : JsonSerializer.Serialize(envelope, EventEnvelope.WireOptions),
            Headers = new Headers
            {
                { "event_id", Encoding.UTF8.GetBytes(envelope.EventId.ToString("D")) },
                { "type", Encoding.UTF8.GetBytes(envelope.Type) },
                { "workspace_id", Encoding.UTF8.GetBytes(envelope.TenantId.ToString("D")) },
            },
        };
        if (!string.IsNullOrEmpty(tenant))
        {
            message.Headers.Add("tenant_id", Encoding.UTF8.GetBytes(tenant));
        }

        if (!string.IsNullOrEmpty(envelope.UserId))
        {
            message.Headers.Add("user_id", Encoding.UTF8.GetBytes(envelope.UserId));
        }

        await _producer.ProduceAsync(item ? _itemsTopic : _topic, message, ct);
    }

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(10));
        _producer.Dispose();
    }
}

/// <summary>Logs and drops. Development only; the API refuses to start with it outside Development.</summary>
public sealed class NullEventBusPublisher : IEventBusPublisher
{
    private readonly ILogger<NullEventBusPublisher> _logger;

    public NullEventBusPublisher(ILogger<NullEventBusPublisher> logger)
    {
        _logger = logger;
    }

    public Task PublishAsync(EventEnvelope envelope, CancellationToken ct)
    {
        _logger.LogInformation("Event dropped by NullEventBusPublisher. Type={Type} EventId={EventId} TenantId={TenantId}", envelope.Type, envelope.EventId, envelope.TenantId);
        return Task.CompletedTask;
    }
}
