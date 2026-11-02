using Parrot.Domain.Entities;
using Parrot.Domain.Enums;
using Parrot.Domain.Exceptions;

namespace Parrot.Application.Customers;

public sealed class CustomerService(ICustomerRepository repository)
{
    /// <summary>
    /// Records or refreshes a customer from an inbound message. Safe to call on
    /// every message: a repeat for the same sender updates their last-seen time
    /// and name; it does not create a second customer row.
    /// </summary>
    public Task UpsertFromMessageAsync(
        string ownerUserId,
        MessagingPlatform platform,
        Guid connectionId,
        string sender,
        string? name,
        string? username,
        DateTime seenAt,
        CancellationToken ct) =>
        repository.UpsertAsync(
            ownerUserId,
            connectionId,
            platform.ToString().ToLowerInvariant(),
            sender,
            name is { Length: > 0 } ? name : sender,
            username,
            seenAt,
            ct);

    public async Task<CustomerPageResponse> ListAsync(
        string ownerUserId,
        string? search,
        string? stage,
        string? channel,
        int skip,
        int take,
        CancellationToken ct,
        IReadOnlySet<Guid>? connections = null)
    {
        (List<Customer> items, int total) = await repository.ListAsync(ownerUserId, search, stage, channel, skip, take, connections, ct);
        return new CustomerPageResponse([.. items.Select(ToListItem)], total);
    }

    /// <param name="connections">
    /// For a team member limited to some channels: a customer who has written on
    /// none of these is not found. Null is every connection.
    /// </param>
    public async Task<CustomerDetailResponse> GetAsync(string ownerUserId, Guid id, CancellationToken ct, IReadOnlySet<Guid>? connections = null)
    {
        Customer? customer = await repository.FindAsync(ownerUserId, id, ct);
        if (customer is null || connections is not null && !customer.Identities.Any(i => connections.Contains(i.ConnectionId)))
            throw new NotFoundException("Customer not found.");
        return ToDetail(customer);
    }

    private static readonly string[] ValidStages = ["New Lead", "Interested", "Customer", "Vip"];
    public static readonly string[] ValidTags = ["Business", "Consumer", "New client", "Returning client", "VIP", "VVIP"];

    public async Task<CustomerDetailResponse> CreateAsync(string ownerUserId, CreateCustomerRequest request, CancellationToken ct)
    {
        List<string> errors = [];
        if (string.IsNullOrWhiteSpace(request.Name)) errors.Add("Name is required.");
        else if (request.Name.Length > 256) errors.Add("Name must be 256 characters or fewer.");

        string stage = string.IsNullOrWhiteSpace(request.Stage) ? "Customer" : request.Stage;
        if (!ValidStages.Contains(stage)) errors.Add("Stage is not valid.");

        List<string> tags = NormalizeTags(request.Tags, errors);

        if (errors.Count > 0) throw new ValidationException(errors);

        Customer customer = new()
        {
            WorkspaceId = ownerUserId,
            Name = request.Name.Trim(),
            Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim(),
            Phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim(),
            ChannelPlatform = string.IsNullOrWhiteSpace(request.ChannelPlatform) ? null : request.ChannelPlatform.Trim(),
            ChannelIdentifier = string.IsNullOrWhiteSpace(request.ChannelIdentifier) ? null : request.ChannelIdentifier.Trim(),
            Stage = stage,
            Tags = tags,
            CreatedAt = DateTime.UtcNow,
        };
        await repository.CreateAsync(customer, ct);
        return ToDetail(customer);
    }

    public async Task<CustomerDetailResponse> UpdateStageAsync(
        string ownerUserId, Guid id, string stage, CancellationToken ct)
    {
        if (!ValidStages.Contains(stage)) throw new ValidationException("Stage is not valid.");
        Customer? customer = await repository.UpdateStageAsync(ownerUserId, id, stage, ct);
        if (customer is null) throw new NotFoundException("Customer not found.");
        return ToDetail(customer);
    }

    public async Task<CustomerDetailResponse> UpdateTagsAsync(
        string ownerUserId, Guid id, List<string> tags, CancellationToken ct)
    {
        List<string> errors = [];
        List<string> normalized = NormalizeTags(tags, errors);
        if (errors.Count > 0) throw new ValidationException(errors);
        Customer? customer = await repository.UpdateTagsAsync(ownerUserId, id, normalized, ct);
        if (customer is null) throw new NotFoundException("Customer not found.");
        return ToDetail(customer);
    }

    // Deduplicates case-insensitively, normalises to canonical casing, and
    // validates that every tag is one of the fixed ValidTags options.
    private static List<string> NormalizeTags(List<string>? raw, List<string> errors)
    {
        List<string> deduped = [.. (raw ?? []).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
        if (deduped.Any(t => !ValidTags.Contains(t, StringComparer.OrdinalIgnoreCase)))
        {
            errors.Add("One or more tags are not valid.");
            return deduped;
        }
        return [.. deduped.Select(t => ValidTags.First(v => v.Equals(t, StringComparison.OrdinalIgnoreCase)))];
    }

    private static CustomerListItemResponse ToListItem(Customer c) => new(
        c.Id, c.Name, c.Stage, c.Tags,
        [.. c.Identities.Select(ToIdentityResponse)],
        c.LastSeenAt, c.LastSeenChannel, c.CreatedAt);

    public async Task<CustomerDetailResponse> UpdateChannelIdentifierAsync(
        string ownerUserId, Guid id, string? channelPlatform, string? channelIdentifier, CancellationToken ct)
    {
        Customer? customer = await repository.UpdateChannelIdentifierAsync(ownerUserId, id, channelPlatform, channelIdentifier, ct);
        if (customer is null) throw new NotFoundException("Customer not found.");
        return ToDetail(customer);
    }

    private static CustomerDetailResponse ToDetail(Customer c) => new(
        c.Id, c.Name, c.Email, c.Stage, c.Tags,
        [.. c.Identities.Select(ToIdentityResponse)],
        c.LastSeenAt, c.LastSeenChannel, c.CreatedAt,
        c.Phone, c.ChannelPlatform, c.ChannelIdentifier);

    // ── Profile links ────────────────────────────────────────────────────

    public async Task<LinkedCustomerProfileResponse> GetLinksAsync(string ownerUserId, Guid id, CancellationToken ct)
    {
        (Customer Primary, List<CustomerLink> Links)? result = await repository.GetLinksAsync(ownerUserId, id, ct);
        if (result is null) throw new NotFoundException("Customer not found.");
        return ToLinkedProfile(result.Value.Primary, result.Value.Links);
    }

    public async Task<CustomerLinkLookupResponse> FindByIdentityAsync(string ownerUserId, Guid connectionId, string sender, CancellationToken ct)
    {
        (Customer Primary, List<CustomerLink> Links)? result = await repository.FindByIdentityAsync(ownerUserId, connectionId, sender, ct);
        return new CustomerLinkLookupResponse(result is null ? null : ToLinkedProfile(result.Value.Primary, result.Value.Links));
    }

    public async Task<LinkedCustomerProfileResponse> LinkAsync(string ownerUserId, Guid primaryId, Guid linkedId, CancellationToken ct)
    {
        if (primaryId == linkedId) throw new ValidationException("A customer cannot be linked to itself.");
        (Customer Primary, List<CustomerLink> Links) result = await repository.LinkAsync(ownerUserId, primaryId, linkedId, ct);
        return ToLinkedProfile(result.Primary, result.Links);
    }

    public async Task<LinkedCustomerProfileResponse> UnlinkAsync(string ownerUserId, Guid primaryId, Guid linkId, CancellationToken ct)
    {
        (Customer Primary, List<CustomerLink> Links) result = await repository.UnlinkAsync(ownerUserId, primaryId, linkId, ct);
        return ToLinkedProfile(result.Primary, result.Links);
    }

    private static LinkedCustomerProfileResponse ToLinkedProfile(Customer primary, List<CustomerLink> links)
    {
        // The primary's Identities already include the linked customers' identities (merged by the repository).
        CustomerDetailResponse primaryDetail = ToDetail(primary);
        List<CustomerLinkResponse> linkResponses = [.. links.Select(l =>
            new CustomerLinkResponse(l.Id, ToDetail(l.LinkedCustomer), l.LinkedAt))];
        return new LinkedCustomerProfileResponse(primaryDetail, linkResponses);
    }

    private static CustomerIdentityResponse ToIdentityResponse(CustomerIdentity i) =>
        new(i.Id, i.ConnectionId, i.Platform, i.Sender, i.Username, i.LinkedAt);
}
