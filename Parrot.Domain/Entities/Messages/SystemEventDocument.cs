using MongoDB.Bson.Serialization.Attributes;
using Parrot.Domain.Enums;

namespace Parrot.Domain.Entities.Messages;

/// <summary>
/// A synthetic event record written to the conversation thread when control changes hands
/// (human takeover, return to AI). Not a real message from any channel — read-only by the inbox.
/// Required base fields are populated with neutral values; only TextBody carries meaning.
/// </summary>
[BsonDiscriminator("system")]
public sealed class SystemEventDocument : MessageDocument
{
    public static SystemEventDocument Create(
        string owner, Guid connectionId, string sender,
        MessagingPlatform platform, string externalAccountId, string channelId, string channelDisplayName,
        string text)
    {
        DateTime now = DateTime.UtcNow;
        return new SystemEventDocument
        {
            OwnerUserId = owner,
            ConnectionId = connectionId,
            MessageId = $"sys:{Guid.NewGuid():N}",
            Platform = platform,
            ExternalAccountId = externalAccountId,
            From = sender,
            Timestamp = now.ToString("O"),
            Type = "system",
            ChannelId = channelId,
            ChannelDisplayName = channelDisplayName,
            TextBody = text,
            Outgoing = false,
            ReceivedAt = now,
            StoredAt = now,
        };
    }
}
