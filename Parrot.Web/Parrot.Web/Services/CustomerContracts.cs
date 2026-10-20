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
    DateTime CreatedAt);

public sealed record CreateCustomerDto(
    string Name,
    string? Email,
    string? Phone,
    string? Stage,
    List<string>? Tags);
