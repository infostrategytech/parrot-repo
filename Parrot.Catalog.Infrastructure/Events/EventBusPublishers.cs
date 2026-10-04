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

    public KafkaEventBusPublisher(IOptions<CatalogOptions> options)
    {
        CatalogRelayOptions relay = options.Value.Relay;
        _topic = relay.Topic;
        _producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = relay.BootstrapServers,
            Acks = Acks.All,
            EnableIdempotence = true,
            MessageSendMaxRetries = 5,
        }).Build();
    }

    public async Task PublishAsync(EventEnvelope envelope, CancellationToken ct)
    {
        Message<string, string> message = new Message<string, string>
        {
            Key = $"{envelope.TenantId:N}:{envelope.AggregateId:N}",
            Value = JsonSerializer.Serialize(envelope, EventEnvelope.WireOptions),
            Headers = new Headers
            {
                { "event_id", System.Text.Encoding.UTF8.GetBytes(envelope.EventId.ToString("D")) },
                { "type", System.Text.Encoding.UTF8.GetBytes(envelope.Type) },
                { "tenant_id", System.Text.Encoding.UTF8.GetBytes(envelope.TenantId.ToString("D")) },
            },
        };
        await _producer.ProduceAsync(_topic, message, ct);
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
