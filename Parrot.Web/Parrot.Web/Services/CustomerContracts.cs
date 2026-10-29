namespace Parrot.Web.Services;

public sealed record CustomerIdentityDto(
    Guid Id,
    Guid ConnectionId,
    string Platform,
    string Sender,
    string? Username,
    DateTime LinkedAt);

public sealed record CustomerListItemDto(
    Guid Id,
    string Name,
    string Stage,
    List<string> Tags,
    List<CustomerIdentityDto> Identities,
    DateTime? LastSeenAt,
    string? LastSeenChannel,
    DateTime CreatedAt);

public sealed record CustomerPageDto(List<CustomerListItemDto> Items, int Total);

public sealed record CustomerDetailDto(
    Guid Id,
    string Name,
    string? Email,
    string Stage,
    List<string> Tags,
    List<CustomerIdentityDto> Identities,
    DateTime? LastSeenAt,
    string? LastSeenChannel,
    DateTime CreatedAt,
    string? Phone,
    string? ChannelPlatform,
    string? ChannelIdentifier)
{
    /// <summary>Optional profile location, when supplied by the API; never inferred from a channel or phone.</summary>
    public string? Location { get; init; }
}

public sealed record CreateCustomerDto(
    string Name,
    string? Email,
    string? Phone,
    string? Stage,
    List<string>? Tags,
    string? ChannelPlatform,
    string? ChannelIdentifier);

public sealed record UpdateStageDto(string Stage);

public sealed record UpdateTagsDto(List<string> Tags);

public sealed record UpdateChannelIdentifierDto(string? ChannelPlatform, string? ChannelIdentifier);
