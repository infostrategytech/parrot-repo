namespace Parrot.Application.Messaging;

/// <summary>
/// Published once the media worker has settled a customer's file, so the owner's open inbox can show it. A signal,
/// not the file: the inbox re-reads the thread, which signs the address. Best effort, like every live push; an
/// inbox that misses it shows the file on its next poll.
/// </summary>
public sealed record MediaSettled
{
    public int SchemaVersion { get; init; } = 1;

    required public string MessageId { get; init; }

    /// <summary>The CRM user to tell.</summary>
    required public string OwnerUserId { get; init; }

    required public Guid ConnectionId { get; init; }

    /// <summary>The customer the message is from, as the inbox groups threads.</summary>
    required public string Customer { get; init; }

    /// <summary>ready, failed or too_large (see MediaStatus).</summary>
    required public string Status { get; init; }

    public string? CorrelationId { get; init; }
}

/// <summary>Announces a <see cref="MediaSettled"/>. Never throws other than for cancellation: the file is already settled.</summary>
public interface IMediaSettledPublisher
{
    Task PublishAsync(MediaSettled settled, CancellationToken cancellationToken = default);
}

/// <summary>
/// Pushed to the owner's inbox when one of a customer's files finishes loading. It names the thread, not the
/// file: the inbox re-reads the thread it has open.
/// </summary>
public sealed record MediaChanged(Guid ConnectionId, string Customer, string MessageId, string Status)
{
    /// <summary>True when this is about the thread the inbox is showing.</summary>
    public bool Concerns(Guid connection, string sender) => ConnectionId == connection && Customer == sender;
}
