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
    public required string Timestamp { get; init; }
    public required string Type { get; init; }
    public string? TextBody { get; init; }
    public required string ChannelId { get; init; }
    public required string ChannelDisplayName { get; init; }
    public string? ContactName { get; init; }

    // Messenger attachment payloads; null for platforms and message types that have none.
    public IReadOnlyList<string>? AttachmentUrls { get; init; }
    public DateTime ReceivedAt { get; init; }
}
