namespace Parrot.Application.Customers;

public sealed record CustomerIdentityResponse(
    Guid Id,
    Guid ConnectionId,
    string Platform,
    string Sender,
    string? Username,
    DateTime LinkedAt);

public sealed record CustomerListItemResponse(
    Guid Id,
    string Name,
    string Stage,
    List<string> Tags,
    List<CustomerIdentityResponse> Identities,
    DateTime? LastSeenAt,
    string? LastSeenChannel,
    DateTime CreatedAt);

public sealed record CustomerDetailResponse(
    Guid Id,
    string Name,
    string? Email,
    string Stage,
    List<string> Tags,
    List<CustomerIdentityResponse> Identities,
    DateTime? LastSeenAt,
    string? LastSeenChannel,
    DateTime CreatedAt,
    string? Phone,
    string? ChannelPlatform,
    string? ChannelIdentifier);

public sealed record CreateCustomerRequest(
    string Name,
    string? Email,
    string? Phone,
    string? Stage,
    List<string>? Tags,
    string? ChannelPlatform,
    string? ChannelIdentifier);

public sealed record CustomerPageResponse(
    List<CustomerListItemResponse> Items,
    int Total);

public sealed record UpdateStageRequest(string Stage);

public sealed record UpdateTagsRequest(List<string> Tags);

public sealed record UpdateChannelIdentifierRequest(string? ChannelPlatform, string? ChannelIdentifier);
