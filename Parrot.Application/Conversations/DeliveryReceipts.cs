namespace Parrot.Application.Conversations;

public enum DeliveryState
{
    /// <summary>The message reached the customer's device.</summary>
    Delivered,

    /// <summary>The customer opened it. Implies delivered.</summary>
    Read,

    /// <summary>The channel could not deliver it after accepting it.</summary>
    Failed,
}

/// <summary>
/// What a channel reports back about a reply we sent, applied to the stored reply. Methods never
/// throw (apart from cancellation): they run on the webhook path, which must answer the channel
/// whatever state the database is in.
/// </summary>
public interface IDeliveryReceipts
{
    /// <summary>
    /// Applies a receipt to the reply the channel knows by <paramref name="providerMessageId"/>, on this
    /// connection only. A receipt can only move a reply forward and keeps the earliest time reported, so
    /// duplicates and out-of-order receipts are harmless. One that arrives before the send has stored the
    /// channel's id is kept for a short while and applied by <see cref="ApplyParkedAsync"/>.
    /// </summary>
    Task RecordAsync(Guid connection, string providerMessageId, DeliveryState state, DateTime at, CancellationToken ct);

    /// <summary>Applies any receipt that arrived before this reply's channel id was stored.</summary>
    Task ApplyParkedAsync(Guid connection, string providerMessageId, CancellationToken ct);
}
