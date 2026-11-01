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

public sealed record LinkCustomerRequest(Guid CustomerId);

/// <summary>One linked profile attached to a primary customer.</summary>
public sealed record CustomerLinkResponse(Guid Id, CustomerDetailResponse Profile, DateTime LinkedAt);

/// <summary>
/// A primary customer together with all profiles linked to them. When the customer
/// is not a primary, <see cref="Links"/> is empty. The <see cref="CustomerDetailResponse.Identities"/>
/// on <see cref="Customer"/> includes identities from all linked profiles as well as its own.
/// </summary>
public sealed record LinkedCustomerProfileResponse(CustomerDetailResponse Customer, List<CustomerLinkResponse> Links);

/// <summary>Result of a resolve lookup: the profile if found, null if the identity is unknown.</summary>
public sealed record CustomerLinkLookupResponse(LinkedCustomerProfileResponse? Profile);
