using Parrot.Domain.Enums;

namespace Parrot.Application.DTOs.Integrations;

public record IncomingMessage
{
    public Guid? ConnectionId { get; init; }
    public string? OwnerUserId { get; init; }
    public required string MessageId { get; init; }
    public required MessagingPlatform Platform { get; init; }
    public required string ExternalAccountId { get; init; }
    public required string From { get; init; }
    // Raw provider value, kept verbatim for provenance. Units differ per platform, so never
    // compare it directly — use SentAt.
    public required string Timestamp { get; init; }

    // Timestamp normalised to UTC. Null when the provider sent nothing usable.
    public DateTime? SentAt { get; init; }
    public required string Type { get; init; }
    public string? TextBody { get; init; }
    public required string ChannelId { get; init; }
    public required string ChannelDisplayName { get; init; }
    public string? ContactName { get; init; }

    // Messenger attachment payloads; null for platforms and message types that have none.
    public IReadOnlyList<string>? AttachmentUrls { get; init; }
    public DateTime ReceivedAt { get; init; }
}
