using MongoDB.Bson.Serialization.Attributes;

namespace Parrot.Domain.Entities.Messages;

/// <summary>
/// An agent the AI service's super agent asked while writing one reply: what it was asked, what it answered, and
/// whether it answered (answered, failed or timed_out). Kept with the reply for the inbox to show; never logged.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class ReplyContributor
{
    public required string AgentId { get; set; }
    public required string Name { get; set; }
    [BsonIgnoreIfNull]
    public string? Avatar { get; set; }
    public required string Question { get; set; }
    public required string Answer { get; set; }
    public required string Status { get; set; }
}
