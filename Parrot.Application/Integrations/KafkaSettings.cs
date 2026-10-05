namespace Parrot.Application.Integrations;

public class KafkaSettings
{
    public const string SectionName = "Kafka";

    public string BootstrapServers { get; set; } = "localhost:9092";
    public string Topic { get; set; } = "whatsapp.messages";
    public string ConsumerGroupId { get; set; } = "whatsapp-ingestor";

    /// <summary>Where the webhooks publish each <c>InboundMessage</c>, keyed by conversation.</summary>
    public string InboundTopic { get; set; } = "messages.inbound";

    /// <summary>Where a <c>MessagePersisted</c> event goes once a message is stored, keyed by conversation.</summary>
    public string PersistedTopic { get; set; } = "messages.persisted";

    /// <summary>Where the message worker puts inbound messages it can never process, with the reason in a header.</summary>
    public string InboundDeadLetterTopic { get; set; } = "messages.inbound.dlq";

    /// <summary>The message worker's consumer group on the inbound topic.</summary>
    public string ProcessorConsumerGroupId { get; set; } = "message-processor";

    /// <summary>The API's own group for reading the persisted topic for realtime delivery; never the worker's.</summary>
    public string RealtimeConsumerGroupId { get; set; } = "realtime-gateway";

    /// <summary>Max messages the consumer collects per round before parallel processing.</summary>
    public int ConsumerBatchSize { get; set; } = 50;

    /// <summary>
    /// Use Google Application Default Credentials (SASL_SSL + OAuthBearer).
    /// Required for GCP Managed Service for Apache Kafka.
    /// </summary>
    public bool UseGcpAuth { get; set; } = false;
}
