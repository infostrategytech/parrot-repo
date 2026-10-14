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

    // Every file the customer sent, on any platform. The decoder describes them; the worker downloads each
    // one and fills in StorageKey before the message is stored. Null when there are none.
    public IReadOnlyList<IncomingMedia>? Media { get; init; }

    // The provider's own id for the conversation, when replying needs it (TikTok does; the raw body is not kept).
    public string? ExternalConversationId { get; init; }
    public DateTime ReceivedAt { get; init; }
}

/// <summary>One file in an <see cref="IncomingMessage"/>. See <see cref="Parrot.Domain.Entities.Messages.MessageAttachment"/>.</summary>
public sealed record IncomingMedia
{
    public required string Kind { get; init; }
    public string? ContentType { get; init; }
    public string? FileName { get; init; }
    public long? SizeBytes { get; init; }
    public string? Caption { get; init; }
    public string? ProviderMediaId { get; init; }
    public string? SourceUrl { get; init; }
    public string? StorageKey { get; init; }
}
