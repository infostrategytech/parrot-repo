using System.Diagnostics;
using Microsoft.Extensions.Options;
using Parrot.Application.Integrations;
using Parrot.Application.Logging;
using Parrot.Domain.Exceptions;

namespace Parrot.Application.Messaging;

/// <summary>
/// Puts the messages from one webhook on the inbound topic and waits for Kafka
/// to confirm each, so the webhook answers 200 only once they are safe.
///
/// If Kafka fails, this throws a 503. WhatsApp resends the whole webhook for
/// up to 7 days; Messenger retries only a few times. Messages already
/// published by then repeat their messageId, so they are still stored once.
/// </summary>
public sealed class InboundMessagePublisher
{
    private readonly IMessageBus _bus;
    private readonly KafkaSettings _kafka;
    private readonly IAppLogger<InboundMessagePublisher> _logger;

    public InboundMessagePublisher(IMessageBus bus, IOptions<KafkaSettings> kafka, IAppLogger<InboundMessagePublisher> logger)
    {
        _bus = bus;
        _kafka = kafka.Value;
        _logger = logger;
    }

    /// <summary>The current request's W3C trace id, carried by every message as its correlationId.</summary>
    public static string CurrentCorrelationId() =>
        (Activity.Current?.TraceId ?? ActivityTraceId.CreateRandom()).ToHexString();

    public async Task PublishAsync(IReadOnlyList<InboundMessage> messages, CancellationToken cancellationToken)
    {
        // One at a time, in the order the provider sent them.
        foreach (InboundMessage message in messages)
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                await _bus.PublishAsync(_kafka.InboundTopic, message.ConversationKey, message, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The reason only: the webhook middleware logs the stack trace
                // once, with the 503 that wraps this.
                _logger.LogError(
                    "message.publish_failed MessageId={MessageId} Source={Source} ConnectionId={ConnectionId} ConversationKey={ConversationKey} Reason={Reason}",
                    message.MessageId,
                    message.Source,
                    message.ConnectionId,
                    message.ConversationKey,
                    ex.Message);

                throw new ServiceUnavailableException("Messages can't be accepted right now. Try again.", ex);
            }

            _logger.LogInformation(
                "message.published MessageId={MessageId} Source={Source} ConnectionId={ConnectionId} ConversationKey={ConversationKey} DurationMs={DurationMs}",
                message.MessageId,
                message.Source,
                message.ConnectionId,
                message.ConversationKey,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }
}
