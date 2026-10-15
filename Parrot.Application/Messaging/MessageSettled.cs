namespace Parrot.Application.Messaging;

/// <summary>What a <see cref="MessageSettled"/> is about.</summary>
public static class SettledKinds
{
    /// <summary>A customer's file finished loading (ready, failed or too large).</summary>
    public const string Media = "media";

    /// <summary>A queued reply was sent, failed or is of unknown outcome.</summary>
    public const string Send = "send";
}

/// <summary>
/// Published by a background worker once it has settled a customer's file or a queued reply, so the owner's open
/// inbox can show it. A signal, not the result: the inbox re-reads the thread, which signs any file address. Best
/// effort, like every live push; an inbox that misses it shows the result on its next poll.
/// </summary>
public sealed record MessageSettled
{
    public int SchemaVersion { get; init; } = 1;

    /// <summary>See <see cref="SettledKinds"/>.</summary>
    required public string Kind { get; init; }

    required public string MessageId { get; init; }

    /// <summary>The CRM user to tell.</summary>
    required public string OwnerUserId { get; init; }

    required public Guid ConnectionId { get; init; }

    /// <summary>The customer the message is from or to, as the inbox groups threads.</summary>
    required public string Customer { get; init; }

    /// <summary>For media: ready, failed or too_large. For a send: accepted, failed or unknown.</summary>
    required public string Status { get; init; }

    public string? CorrelationId { get; init; }
}

/// <summary>Announces a <see cref="MessageSettled"/>. Never throws other than for cancellation: the work is already settled.</summary>
public interface IMessageSettledPublisher
{
    Task PublishAsync(MessageSettled settled, CancellationToken cancellationToken = default);
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
