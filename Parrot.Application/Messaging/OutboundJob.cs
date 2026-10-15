using Parrot.Application.DTOs.Integrations;
using Parrot.Domain.Entities.Messages;

namespace Parrot.Application.Messaging;

/// <summary>
/// One reply for the send worker to hand to its channel, queued once the API has stored the reply and its file. It
/// names the reply and nothing else: the worker reads the message back for the recipient and the file, and the
/// connection's token from the database, so no secret or customer detail travels through Kafka.
///
/// It can arrive more than once (the API queues it again for a reply left pending, and a replay delivers it twice);
/// the worker claims the reply before it sends, so only one delivery ever reaches the channel.
/// </summary>
public sealed record OutboundJob
{
    public int SchemaVersion { get; init; } = 1;

    required public string MessageId { get; init; }

    /// <summary>The thread the reply is in. It is the partition key, so one thread's replies are sent in the order they were queued.</summary>
    required public string ThreadKey { get; init; }

    public string? CorrelationId { get; init; }

    required public DateTime EnqueuedAt { get; init; }

    public static string KeyFor(Guid connection, string recipient) => $"{connection:N}:{recipient}";
}

/// <summary>Queues an <see cref="OutboundJob"/>. It throws if Kafka does not take it, and the caller then fails the reply cleanly.</summary>
public interface IOutboundJobPublisher
{
    Task PublishAsync(OutboundJob job, CancellationToken cancellationToken = default);
}

/// <summary>What handing a stored reply to its channel came to.</summary>
/// <param name="ProviderId">The channel's id for the message when it accepted it.</param>
/// <param name="RejectedCode">The channel's error code, or one of ours (file, connection, token, channel), when nothing was sent.</param>
public sealed record FileDispatchResult(string? ProviderId, string? RejectedCode)
{
    /// <summary>The picture or file could not be uploaded to the channel: nothing has reached the customer, so asking again is safe.</summary>
    public const string UploadFailed = "upload";

    public bool IsSafeToRetry => RejectedCode == UploadFailed;
}

/// <summary>
/// Reads a stored reply's file and hands it to the channel it is for, with the connection's own token. It reports a
/// refusal as a result and lets a dropped or timed-out call throw, because those two mean different things: a
/// refusal means nothing was sent, a throw means the customer may or may not have it.
/// </summary>
public interface IOutboundFileDispatcher
{
    Task<FileDispatchResult> DispatchAsync(MessageDocument reply, CancellationToken cancellationToken);
}
