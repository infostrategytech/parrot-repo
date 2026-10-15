namespace Parrot.Application.Integrations;

public class KafkaSettings
{
    public const string SectionName = "Kafka";

    public string BootstrapServers { get; set; } = "localhost:9092";
    public string ConsumerGroupId { get; set; } = "whatsapp-ingestor";

    /// <summary>Where the webhooks publish each <c>InboundMessage</c>, keyed by conversation.</summary>
    public string InboundTopic { get; set; } = "messages.inbound";

    /// <summary>Where a <c>MessagePersisted</c> event goes once a message is stored, keyed by conversation.</summary>
    public string PersistedTopic { get; set; } = "messages.persisted";

    /// <summary>Where the message worker puts inbound messages it can never process, with the reason in a header.</summary>
    public string InboundDeadLetterTopic { get; set; } = "messages.inbound.dlq";

    /// <summary>Where the message worker queues one job per customer file to copy into private storage, keyed by message.</summary>
    public string MediaTopic { get; set; } = "messages.media";

    /// <summary>Media jobs that can never succeed (the message is gone, the file's position is wrong), with the reason in a header.</summary>
    public string MediaDeadLetterTopic { get; set; } = "messages.media.dlq";

    /// <summary>The media workers' consumer group. Every worker instance shares it, so the jobs are split between them.</summary>
    public string MediaConsumerGroupId { get; set; } = "media-worker";

    /// <summary>The message worker's consumer group on the inbound topic.</summary>
    public string ProcessorConsumerGroupId { get; set; } = "message-processor";

    /// <summary>The API's own group for reading the persisted topic for realtime delivery; never the worker's.</summary>
    public string RealtimeConsumerGroupId { get; set; } = "realtime-gateway";

    /// <summary>
    /// Where the AI orchestrator reports what its agents did (turns, approvals,
    /// escalations), keyed by tenant, and where the API announces a changed
    /// agent configuration. Read by the API into the activity store.
    /// </summary>
    public string AgentEventsTopic { get; set; } = "agent.events";

    /// <summary>The API's group on the agent events topic; it reads from the beginning, since every event is kept.</summary>
    public string AgentEventsConsumerGroupId { get; set; } = "parrot-agent-events";

    /// <summary>
    /// Use Google Application Default Credentials (SASL_SSL + OAuthBearer).
    /// Required for GCP Managed Service for Apache Kafka.
    /// </summary>
    public bool UseGcpAuth { get; set; } = false;
}
