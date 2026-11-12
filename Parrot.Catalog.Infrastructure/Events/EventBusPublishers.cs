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
    /// Row events go to catalog.events and whole-item events to catalog.items, in the same
    /// shape (<see cref="CatalogEventMessage"/>): tenant_id is the owner, workspace_id the
    /// business. Both are keyed by workspace and aggregate, so one product's events stay ordered.
    /// </summary>
    public async Task PublishAsync(EventEnvelope envelope, CancellationToken ct)
    {
        Message<string, string> message = new Message<string, string>
        {
            Key = $"{envelope.WorkspaceId:N}:{envelope.AggregateId:N}",
            Value = JsonSerializer.Serialize(CatalogEventMessage.From(envelope), EventEnvelope.WireOptions),
            Headers = new Headers
            {
                { "event_id", Encoding.UTF8.GetBytes(envelope.EventId.ToString("D")) },
                { "type", Encoding.UTF8.GetBytes(envelope.Type) },
                { "workspace_id", Encoding.UTF8.GetBytes(envelope.WorkspaceId.ToString("D")) },
            },
        };
        if (!string.IsNullOrEmpty(envelope.OwnerId))
        {
            message.Headers.Add("tenant_id", Encoding.UTF8.GetBytes(envelope.OwnerId));
        }

        if (!string.IsNullOrEmpty(envelope.UserId))
        {
            message.Headers.Add("user_id", Encoding.UTF8.GetBytes(envelope.UserId));
        }

        await _producer.ProduceAsync(CatalogEventTypes.IsItemEvent(envelope.Type) ? _itemsTopic : _topic, message, ct);
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
        _logger.LogInformation("Event dropped by NullEventBusPublisher. Type={Type} EventId={EventId} WorkspaceId={WorkspaceId}", envelope.Type, envelope.EventId, envelope.WorkspaceId);
        return Task.CompletedTask;
    }
}
