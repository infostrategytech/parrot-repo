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
        CancellationToken ct)
    {
        (List<Customer> items, int total) = await repository.ListAsync(ownerUserId, search, stage, channel, skip, take, ct);
        return new CustomerPageResponse([.. items.Select(ToListItem)], total);
    }

    public async Task<CustomerDetailResponse> GetAsync(string ownerUserId, Guid id, CancellationToken ct)
    {
        Customer? customer = await repository.FindAsync(ownerUserId, id, ct);
        if (customer is null) throw new NotFoundException("Customer not found.");
        return ToDetail(customer);
    }

    public async Task<CustomerDetailResponse> CreateAsync(string ownerUserId, CreateCustomerRequest request, CancellationToken ct)
    {
        List<string> errors = [];
        if (string.IsNullOrWhiteSpace(request.Name)) errors.Add("Name is required.");
        else if (request.Name.Length > 256) errors.Add("Name must be 256 characters or fewer.");
        if (errors.Count > 0) throw new ValidationException(errors);

        Customer customer = new()
        {
            OwnerUserId = ownerUserId,
            Name = request.Name.Trim(),
            Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim(),
            Stage = string.IsNullOrWhiteSpace(request.Stage) ? "Customer" : request.Stage,
            Tags = request.Tags ?? [],
            CreatedAt = DateTime.UtcNow,
        };
        await repository.CreateAsync(customer, ct);
        return ToDetail(customer);
    }

    private static readonly string[] ValidStages = ["New Lead", "Interested", "Customer", "Vip"];

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
        List<string> trimmed = [.. tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
        if (trimmed.Any(t => t.Length > 64)) throw new ValidationException("Each tag must be 64 characters or fewer.");
        Customer? customer = await repository.UpdateTagsAsync(ownerUserId, id, trimmed, ct);
        if (customer is null) throw new NotFoundException("Customer not found.");
        return ToDetail(customer);
    }

    private static CustomerListItemResponse ToListItem(Customer c) => new(
        c.Id, c.Name, c.Stage, c.Tags,
        [.. c.Identities.Select(ToIdentityResponse)],
        c.LastSeenAt, c.LastSeenChannel, c.CreatedAt);

    private static CustomerDetailResponse ToDetail(Customer c) => new(
        c.Id, c.Name, c.Email, c.Stage, c.Tags,
        [.. c.Identities.Select(ToIdentityResponse)],
        c.LastSeenAt, c.LastSeenChannel, c.CreatedAt);

    private static CustomerIdentityResponse ToIdentityResponse(CustomerIdentity i) =>
        new(i.Id, i.ConnectionId, i.Platform, i.Sender, i.Username, i.LinkedAt);
}
