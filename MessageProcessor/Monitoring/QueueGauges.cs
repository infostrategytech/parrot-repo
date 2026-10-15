using Microsoft.Extensions.Options;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;
using Parrot.Infrastructure.MessageBus;

namespace MessageProcessor.Monitoring;

/// <summary>
/// Feeds message_queue_depth (the worker's lag on the inbound topic) and
/// message_dead_letter_depth (dead letters nobody has reviewed yet). Kafka is
/// asked every <see cref="Interval"/> and the gauges report the cached answer,
/// so a scrape never waits on the broker. While Kafka can't be asked, the
/// gauges report nothing rather than a stale number.
/// </summary>
public sealed class QueueGauges(IMessageBus bus, IOptions<KafkaSettings> kafka, WorkerRoles roles, ILogger<QueueGauges> logger) : BackgroundService
{
    /// <summary>
    /// The consumer group whose committed offset marks the dead letters someone has
    /// dealt with. Kafka keeps records after they are read, so "held on the topic"
    /// would never return to 0; the depth is what lies past this group's commit.
    /// After fixing and replaying, move it to the end (see monitoring/README.md).
    /// </summary>
    public const string DeadLetterReviewGroup = "message-processor-dlq-reviewed";

    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    // One entry per queue this process works on. -1 while Kafka can't be asked.
    private sealed class Watched(string topic, string group, string deadLetterTopic)
    {
        public string Topic { get; } = topic;
        public string Group { get; } = group;
        public string DeadLetterTopic { get; } = deadLetterTopic;
        public long Depth = -1;
        public long DeadLetters = -1;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The host waits for ExecuteAsync to reach its first await; the Kafka
        // queries below can block for seconds, so let startup go on first.
        await Task.Yield();
        if (bus is not KafkaMessageBus kafkaBus) return;

        KafkaSettings settings = kafka.Value;
        List<Watched> watched = [];
        if (roles.HasFlag(WorkerRoles.Messages)) watched.Add(new(settings.InboundTopic, settings.ProcessorConsumerGroupId, settings.InboundDeadLetterTopic));
        if (roles.HasFlag(WorkerRoles.Media)) watched.Add(new(settings.MediaTopic, settings.MediaConsumerGroupId, settings.MediaDeadLetterTopic));
        if (roles.HasFlag(WorkerRoles.Outbound)) watched.Add(new(settings.OutboundTopic, settings.OutboundConsumerGroupId, settings.OutboundDeadLetterTopic));

        MessageMetrics.RegisterQueueGauges([.. watched.Select(w => new MessageMetrics.QueueProbe(
            w.Topic, w.DeadLetterTopic, () => Known(Interlocked.Read(ref w.Depth)), () => Known(Interlocked.Read(ref w.DeadLetters))))]);

        using PeriodicTimer timer = new PeriodicTimer(Interval);
        do
        {
            foreach (Watched w in watched)
            {
                try
                {
                    Interlocked.Exchange(ref w.Depth, await kafkaBus.ConsumerLagAsync(w.Topic, w.Group, Interval));
                    Interlocked.Exchange(ref w.DeadLetters, await kafkaBus.ConsumerLagAsync(w.DeadLetterTopic, DeadLetterReviewGroup, Interval));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Interlocked.Exchange(ref w.Depth, -1);
                    Interlocked.Exchange(ref w.DeadLetters, -1);
                    logger.LogWarning("Queue gauges not refreshed for {Topic}. Reason={Reason}", w.Topic, ex.Message);
                }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private static long? Known(long value) => value < 0 ? null : value;
}
