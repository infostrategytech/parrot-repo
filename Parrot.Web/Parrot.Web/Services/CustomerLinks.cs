namespace Parrot.Web.Services;

/// <summary>A reversible profile association, not a merge of messages or contact fields.</summary>
public sealed record CustomerProfileLink(Guid Id, CustomerDetailDto Profile, DateTime LinkedAt);
public sealed record LinkedCustomerProfile(CustomerDetailDto Customer, List<CustomerProfileLink> Links);
public sealed record CustomerLinkLookup(LinkedCustomerProfile? Profile);
public sealed record LinkCustomerRequest(Guid CustomerId);

/// <summary>Frontend contract awaiting backend support. Never match people by display name or photo.</summary>
public interface ICustomerLinks
{
    bool Available { get; }
    Task<LinkedCustomerProfile> GetAsync(Guid customerId, CancellationToken ct);
    Task<LinkedCustomerProfile?> FindAsync(Guid connectionId, string sender, CancellationToken ct);
    Task<LinkedCustomerProfile> LinkAsync(Guid sourceId, Guid targetId, CancellationToken ct);
    Task<LinkedCustomerProfile> UnlinkAsync(Guid customerId, Guid linkId, CancellationToken ct);
}

/// <summary>Uses proposed endpoints, registered only with the local demo handler until the API supports them.</summary>
public sealed class CustomerLinkClient(CustomerClient client) : ICustomerLinks
{
    public bool Available => true;
    public Task<LinkedCustomerProfile> GetAsync(Guid customerId, CancellationToken ct) =>
        client.RequestAsync<LinkedCustomerProfile>(HttpMethod.Get, $"api/customers/{customerId}/links", null, ct);

    public async Task<LinkedCustomerProfile?> FindAsync(Guid connectionId, string sender, CancellationToken ct) =>
        (await client.RequestAsync<CustomerLinkLookup>(HttpMethod.Get,
            $"api/customers/resolve?connectionId={connectionId}&sender={Uri.EscapeDataString(sender)}", null, ct)).Profile;

    public Task<LinkedCustomerProfile> LinkAsync(Guid sourceId, Guid targetId, CancellationToken ct) =>
        client.RequestAsync<LinkedCustomerProfile>(HttpMethod.Post, $"api/customers/{sourceId}/links", new LinkCustomerRequest(targetId), ct);

    public Task<LinkedCustomerProfile> UnlinkAsync(Guid customerId, Guid linkId, CancellationToken ct) =>
        client.RequestAsync<LinkedCustomerProfile>(HttpMethod.Delete, $"api/customers/{customerId}/links/{linkId}", null, ct);
}

public sealed class UnavailableCustomerLinks : ICustomerLinks
{
    public bool Available => false;
    public Task<LinkedCustomerProfile> GetAsync(Guid customerId, CancellationToken ct) => throw new NotSupportedException();
    public Task<LinkedCustomerProfile?> FindAsync(Guid connectionId, string sender, CancellationToken ct) => throw new NotSupportedException();
    public Task<LinkedCustomerProfile> LinkAsync(Guid sourceId, Guid targetId, CancellationToken ct) => throw new NotSupportedException();
    public Task<LinkedCustomerProfile> UnlinkAsync(Guid customerId, Guid linkId, CancellationToken ct) => throw new NotSupportedException();
}
