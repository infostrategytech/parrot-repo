namespace Parrot.Application.Messaging;

/// <summary>
/// One customer file to copy into private storage, queued by the message worker once the message is stored.
/// It names the file and nothing else: the media worker reads the message back for the platform, the account
/// and the file's address, so no signed link or customer detail travels through Kafka.
///
/// It can arrive more than once (a replay publishes it again); the worker skips a file that is no longer pending.
/// </summary>
public sealed record MediaJob
{
    public int SchemaVersion { get; init; } = 1;

    required public string MessageId { get; init; }

    /// <summary>The file's position in the message's attachments.</summary>
    required public int Index { get; init; }

    /// <summary>The trace id of the webhook request that brought the message in.</summary>
    required public string CorrelationId { get; init; }

    required public DateTime EnqueuedAt { get; init; }
}

/// <summary>Queues a <see cref="MediaJob"/>, keyed by message so the files of every message spread across the partitions.</summary>
public interface IMediaJobPublisher
{
    Task PublishAsync(MediaJob job, CancellationToken cancellationToken = default);
}
