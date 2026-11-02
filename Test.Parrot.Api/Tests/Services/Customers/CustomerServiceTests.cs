using Parrot.Application.Customers;
using Parrot.Domain.Entities;
using Parrot.Domain.Enums;
using Parrot.Domain.Exceptions;

namespace Test.Parrot.Api.Tests.Services.Customers;

/// <summary>
/// Unit tests for CustomerService. The repository is a simple in-memory fake
/// — no Postgres or Moq required here.
/// </summary>
public class CustomerServiceTests
{
    private const string Owner = "owner-1";
    private static readonly Guid ConnectionId = Guid.NewGuid();
    private readonly FakeCustomerRepository _repository = new();
    private readonly CustomerService _service;

    public CustomerServiceTests()
    {
        _service = new CustomerService(_repository);
    }

    // ---- UpsertFromMessageAsync ----

    [Fact]
    public async Task UpsertFromMessageAsync_NewSender_CreatesOneCustomer()
    {
        await _service.UpsertFromMessageAsync(Owner, MessagingPlatform.WhatsApp, ConnectionId,
            "+2348012345678", "Amaka Obi", null, DateTime.UtcNow, CancellationToken.None);

        Assert.Single(_repository.UpsertCalls);
        FakeCustomerRepository.UpsertCall call = _repository.UpsertCalls[0];
        Assert.Equal(Owner, call.OwnerUserId);
        Assert.Equal(ConnectionId, call.ConnectionId);
        Assert.Equal("whatsapp", call.Platform);
        Assert.Equal("+2348012345678", call.Sender);
        Assert.Equal("Amaka Obi", call.Name);
    }

    [Fact]
    public async Task UpsertFromMessageAsync_NullName_FallsBackToSender()
    {
        await _service.UpsertFromMessageAsync(Owner, MessagingPlatform.Facebook, ConnectionId,
            "1234567890", null, null, DateTime.UtcNow, CancellationToken.None);

        Assert.Equal("1234567890", _repository.UpsertCalls[0].Name);
    }

    [Fact]
    public async Task UpsertFromMessageAsync_EmptyName_FallsBackToSender()
    {
        await _service.UpsertFromMessageAsync(Owner, MessagingPlatform.Instagram, ConnectionId,
            "igsid_abc", "", "@handle", DateTime.UtcNow, CancellationToken.None);

        Assert.Equal("igsid_abc", _repository.UpsertCalls[0].Name);
        Assert.Equal("@handle", _repository.UpsertCalls[0].Username);
    }

    [Fact]
    public async Task UpsertFromMessageAsync_SameSenderTwice_CallsRepositoryTwice()
    {
        // The deduplication is in the repository (and its unique index), not in the service.
        // The service's job is to translate and delegate — both calls must reach the repository.
        DateTime first = new(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);
        DateTime second = new(2026, 10, 1, 11, 0, 0, DateTimeKind.Utc);

        await _service.UpsertFromMessageAsync(Owner, MessagingPlatform.WhatsApp, ConnectionId,
            "+234", "Name", null, first, CancellationToken.None);
        await _service.UpsertFromMessageAsync(Owner, MessagingPlatform.WhatsApp, ConnectionId,
            "+234", "Name Updated", null, second, CancellationToken.None);

        Assert.Equal(2, _repository.UpsertCalls.Count);
    }

    [Fact]
    public async Task UpsertFromMessageAsync_PlatformIsLowercased()
    {
        await _service.UpsertFromMessageAsync(Owner, MessagingPlatform.TikTok, ConnectionId,
            "tiktok_id", "Creator", "handle", DateTime.UtcNow, CancellationToken.None);

        Assert.Equal("tiktok", _repository.UpsertCalls[0].Platform);
    }

    // ---- GetAsync ----

    [Fact]
    public async Task GetAsync_ExistingCustomer_ReturnsDetail()
    {
        Guid id = Guid.NewGuid();
        _repository.Seed(new Customer { Id = id, WorkspaceId = Owner, Name = "Test User", Stage = "Customer" });

        CustomerDetailResponse result = await _service.GetAsync(Owner, id, CancellationToken.None);

        Assert.Equal(id, result.Id);
        Assert.Equal("Test User", result.Name);
        Assert.Equal("Customer", result.Stage);
    }

    [Fact]
    public async Task GetAsync_NotFound_ThrowsNotFoundException()
    {
        await Assert.ThrowsAsync<NotFoundException>(() =>
            _service.GetAsync(Owner, Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task GetAsync_AnotherOwnersId_ThrowsNotFoundException()
    {
        // Another owner's customer must be indistinguishable from a missing row.
        Guid id = Guid.NewGuid();
        _repository.Seed(new Customer { Id = id, WorkspaceId = "other-owner", Name = "Theirs", Stage = "Customer" });

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _service.GetAsync(Owner, id, CancellationToken.None));
    }

    // ---- CreateAsync ----

    [Fact]
    public async Task CreateAsync_ValidRequest_CreatesAndReturnsCustomer()
    {
        CreateCustomerRequest request = new("Ada Lovelace", "ada@example.com", null, "New Lead", ["VIP"], null, null);

        CustomerDetailResponse result = await _service.CreateAsync(Owner, request, CancellationToken.None);

        Assert.Equal("Ada Lovelace", result.Name);
        Assert.Equal("ada@example.com", result.Email);
        Assert.Equal("New Lead", result.Stage);
        Assert.Contains("VIP", result.Tags);
        Assert.Single(_repository.Created);
        Assert.Equal(Owner, _repository.Created[0].WorkspaceId);
    }

    [Fact]
    public async Task CreateAsync_NullStage_DefaultsToCustomer()
    {
        CreateCustomerRequest request = new("Buyer", null, null, null, null, null, null);

        CustomerDetailResponse result = await _service.CreateAsync(Owner, request, CancellationToken.None);

        Assert.Equal("Customer", result.Stage);
    }

    [Fact]
    public async Task CreateAsync_EmptyName_ThrowsValidationException()
    {
        CreateCustomerRequest request = new("  ", null, null, null, null, null, null);

        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.CreateAsync(Owner, request, CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_InvalidStage_ThrowsValidationException()
    {
        CreateCustomerRequest request = new("Ada", null, null, "Hacked", null, null, null);

        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.CreateAsync(Owner, request, CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_InvalidTag_ThrowsValidationException()
    {
        CreateCustomerRequest request = new("Ada", null, null, null, ["NotATag"], null, null);

        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.CreateAsync(Owner, request, CancellationToken.None));
    }

    // ---- UpdateStageAsync ----

    [Fact]
    public async Task UpdateStageAsync_ValidStage_UpdatesAndReturns()
    {
        Guid id = Guid.NewGuid();
        _repository.Seed(new Customer { Id = id, WorkspaceId = Owner, Name = "Ada", Stage = "Customer" });

        CustomerDetailResponse result = await _service.UpdateStageAsync(Owner, id, "Vip", CancellationToken.None);

        Assert.Equal("Vip", result.Stage);
        Assert.Equal("Vip", _repository.Store.First(c => c.Id == id).Stage);
    }

    [Fact]
    public async Task UpdateStageAsync_InvalidStage_ThrowsValidationException()
    {
        Guid id = Guid.NewGuid();
        _repository.Seed(new Customer { Id = id, WorkspaceId = Owner, Name = "Ada", Stage = "Customer" });

        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.UpdateStageAsync(Owner, id, "Unknown", CancellationToken.None));
    }

    [Fact]
    public async Task UpdateStageAsync_AnotherOwnersId_ThrowsNotFoundException()
    {
        Guid id = Guid.NewGuid();
        _repository.Seed(new Customer { Id = id, WorkspaceId = "other", Name = "Theirs", Stage = "Customer" });

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _service.UpdateStageAsync(Owner, id, "Vip", CancellationToken.None));
    }

    // ---- UpdateTagsAsync ----

    [Fact]
    public async Task UpdateTagsAsync_SetsTags()
    {
        Guid id = Guid.NewGuid();
        _repository.Seed(new Customer { Id = id, WorkspaceId = Owner, Name = "Ada", Stage = "Customer" });

        CustomerDetailResponse result = await _service.UpdateTagsAsync(Owner, id, ["VIP", "Consumer"], CancellationToken.None);

        Assert.Equal(["VIP", "Consumer"], result.Tags);
    }

    [Fact]
    public async Task UpdateTagsAsync_TrimsAndDeduplicatesCaseInsensitive()
    {
        Guid id = Guid.NewGuid();
        _repository.Seed(new Customer { Id = id, WorkspaceId = Owner, Name = "Ada", Stage = "Customer" });

        CustomerDetailResponse result = await _service.UpdateTagsAsync(Owner, id, ["  VIP  ", "vip", "Consumer"], CancellationToken.None);

        // "VIP" and "vip" collapse to one after trim+dedup; canonical casing is preserved
        Assert.Equal(2, result.Tags.Count);
        Assert.Contains("VIP", result.Tags);
        Assert.Contains("Consumer", result.Tags);
    }

    [Fact]
    public async Task UpdateTagsAsync_InvalidTag_ThrowsValidationException()
    {
        Guid id = Guid.NewGuid();
        _repository.Seed(new Customer { Id = id, WorkspaceId = Owner, Name = "Ada", Stage = "Customer" });

        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.UpdateTagsAsync(Owner, id, ["VIP", "NotATag"], CancellationToken.None));
    }

    [Fact]
    public async Task UpdateTagsAsync_AnotherOwnersId_ThrowsNotFoundException()
    {
        Guid id = Guid.NewGuid();
        _repository.Seed(new Customer { Id = id, WorkspaceId = "other", Name = "Theirs", Stage = "Customer" });

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _service.UpdateTagsAsync(Owner, id, ["VIP"], CancellationToken.None));
    }

    // ---- GetLinksAsync ----

    [Fact]
    public async Task GetLinksAsync_PrimaryWithNoLinks_ReturnsEmptyLinks()
    {
        Guid id = Guid.NewGuid();
        _repository.Seed(new Customer { Id = id, WorkspaceId = Owner, Name = "Ada", Stage = "Customer" });

        LinkedCustomerProfileResponse result = await _service.GetLinksAsync(Owner, id, CancellationToken.None);

        Assert.Equal(id, result.Customer.Id);
        Assert.Empty(result.Links);
    }

    [Fact]
    public async Task GetLinksAsync_PrimaryWithLinks_ReturnsLinkedProfiles()
    {
        Guid primaryId = Guid.NewGuid();
        Guid linkedId = Guid.NewGuid();
        Customer primary = new() { Id = primaryId, WorkspaceId = Owner, Name = "Ada", Stage = "Customer" };
        Customer linked = new() { Id = linkedId, WorkspaceId = Owner, Name = "Ada Instagram", Stage = "Customer" };
        _repository.Seed(primary);
        _repository.Seed(linked);
        _repository.SeedLink(new CustomerLink { Id = Guid.NewGuid(), WorkspaceId = Owner, PrimaryCustomerId = primaryId, LinkedCustomerId = linkedId, LinkedCustomer = linked });

        LinkedCustomerProfileResponse result = await _service.GetLinksAsync(Owner, primaryId, CancellationToken.None);

        Assert.Equal(primaryId, result.Customer.Id);
        Assert.Single(result.Links);
        Assert.Equal(linkedId, result.Links[0].Profile.Id);
    }

    [Fact]
    public async Task GetLinksAsync_NotFound_ThrowsNotFoundException()
    {
        await Assert.ThrowsAsync<NotFoundException>(() =>
            _service.GetLinksAsync(Owner, Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task GetLinksAsync_AnotherOwnersId_ThrowsNotFoundException()
    {
        Guid id = Guid.NewGuid();
        _repository.Seed(new Customer { Id = id, WorkspaceId = "other", Name = "Theirs", Stage = "Customer" });

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _service.GetLinksAsync(Owner, id, CancellationToken.None));
    }

    // ---- LinkAsync ----

    [Fact]
    public async Task LinkAsync_ValidPair_CreatesLinkAndReturnsPrimary()
    {
        Guid primaryId = Guid.NewGuid();
        Guid linkedId = Guid.NewGuid();
        _repository.Seed(new Customer { Id = primaryId, WorkspaceId = Owner, Name = "Ada WA", Stage = "Customer" });
        _repository.Seed(new Customer { Id = linkedId, WorkspaceId = Owner, Name = "Ada IG", Stage = "Customer" });

        LinkedCustomerProfileResponse result = await _service.LinkAsync(Owner, primaryId, linkedId, CancellationToken.None);

        Assert.Equal(primaryId, result.Customer.Id);
        Assert.Single(result.Links);
        Assert.Equal(linkedId, result.Links[0].Profile.Id);
    }

    [Fact]
    public async Task LinkAsync_SameId_ThrowsValidationException()
    {
        Guid id = Guid.NewGuid();
        _repository.Seed(new Customer { Id = id, WorkspaceId = Owner, Name = "Ada", Stage = "Customer" });

        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.LinkAsync(Owner, id, id, CancellationToken.None));
    }

    [Fact]
    public async Task LinkAsync_PrimaryNotFound_ThrowsNotFoundException()
    {
        Guid linkedId = Guid.NewGuid();
        _repository.Seed(new Customer { Id = linkedId, WorkspaceId = Owner, Name = "Ada", Stage = "Customer" });

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _service.LinkAsync(Owner, Guid.NewGuid(), linkedId, CancellationToken.None));
    }

    [Fact]
    public async Task LinkAsync_LinkedNotFound_ThrowsNotFoundException()
    {
        Guid primaryId = Guid.NewGuid();
        _repository.Seed(new Customer { Id = primaryId, WorkspaceId = Owner, Name = "Ada", Stage = "Customer" });

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _service.LinkAsync(Owner, primaryId, Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task LinkAsync_PrimaryIsAlreadyLinkedToAnotherPrimary_ThrowsConflictException()
    {
        Guid grandprimaryId = Guid.NewGuid();
        Guid primaryId = Guid.NewGuid();
        Guid linkedId = Guid.NewGuid();
        Customer grandprimary = new() { Id = grandprimaryId, WorkspaceId = Owner, Name = "Grand", Stage = "Customer" };
        Customer primary = new() { Id = primaryId, WorkspaceId = Owner, Name = "Ada", Stage = "Customer" };
        Customer linked = new() { Id = linkedId, WorkspaceId = Owner, Name = "Ada IG", Stage = "Customer" };
        _repository.Seed(grandprimary);
        _repository.Seed(primary);
        _repository.Seed(linked);
        // primaryId is the linked side of an existing link; it can't itself become a primary.
        _repository.SeedLink(new CustomerLink { Id = Guid.NewGuid(), WorkspaceId = Owner, PrimaryCustomerId = grandprimaryId, LinkedCustomerId = primaryId, LinkedCustomer = primary });

        await Assert.ThrowsAsync<ConflictException>(() =>
            _service.LinkAsync(Owner, primaryId, linkedId, CancellationToken.None));
    }

    [Fact]
    public async Task LinkAsync_LinkedAlreadyHasLinkedProfiles_ThrowsConflictException()
    {
        Guid primaryId = Guid.NewGuid();
        Guid linkedId = Guid.NewGuid();
        Guid subLinkedId = Guid.NewGuid();
        Customer primary = new() { Id = primaryId, WorkspaceId = Owner, Name = "Ada", Stage = "Customer" };
        Customer linked = new() { Id = linkedId, WorkspaceId = Owner, Name = "Ada IG", Stage = "Customer" };
        Customer subLinked = new() { Id = subLinkedId, WorkspaceId = Owner, Name = "Ada TikTok", Stage = "Customer" };
        _repository.Seed(primary);
        _repository.Seed(linked);
        _repository.Seed(subLinked);
        // linkedId already has its own links — a chain is not allowed.
        _repository.SeedLink(new CustomerLink { Id = Guid.NewGuid(), WorkspaceId = Owner, PrimaryCustomerId = linkedId, LinkedCustomerId = subLinkedId, LinkedCustomer = subLinked });

        await Assert.ThrowsAsync<ConflictException>(() =>
            _service.LinkAsync(Owner, primaryId, linkedId, CancellationToken.None));
    }

    // ---- UnlinkAsync ----

    [Fact]
    public async Task UnlinkAsync_ExistingLink_RemovesItAndReturnsUpdatedPrimary()
    {
        Guid primaryId = Guid.NewGuid();
        Guid linkedId = Guid.NewGuid();
        Guid linkId = Guid.NewGuid();
        Customer primary = new() { Id = primaryId, WorkspaceId = Owner, Name = "Ada", Stage = "Customer" };
        Customer linked = new() { Id = linkedId, WorkspaceId = Owner, Name = "Ada IG", Stage = "Customer" };
        _repository.Seed(primary);
        _repository.Seed(linked);
        _repository.SeedLink(new CustomerLink { Id = linkId, WorkspaceId = Owner, PrimaryCustomerId = primaryId, LinkedCustomerId = linkedId, LinkedCustomer = linked });

        LinkedCustomerProfileResponse result = await _service.UnlinkAsync(Owner, primaryId, linkId, CancellationToken.None);

        Assert.Equal(primaryId, result.Customer.Id);
        Assert.Empty(result.Links);
    }

    [Fact]
    public async Task UnlinkAsync_PrimaryNotFound_ThrowsNotFoundException()
    {
        await Assert.ThrowsAsync<NotFoundException>(() =>
            _service.UnlinkAsync(Owner, Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task UnlinkAsync_LinkNotFound_ThrowsNotFoundException()
    {
        Guid primaryId = Guid.NewGuid();
        _repository.Seed(new Customer { Id = primaryId, WorkspaceId = Owner, Name = "Ada", Stage = "Customer" });

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _service.UnlinkAsync(Owner, primaryId, Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task UnlinkAsync_AnotherOwnersPrimary_ThrowsNotFoundException()
    {
        Guid primaryId = Guid.NewGuid();
        _repository.Seed(new Customer { Id = primaryId, WorkspaceId = "other", Name = "Theirs", Stage = "Customer" });

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _service.UnlinkAsync(Owner, primaryId, Guid.NewGuid(), CancellationToken.None));
    }

    // --------------- fake ---------------

    private sealed class FakeCustomerRepository : ICustomerRepository
    {
        public List<UpsertCall> UpsertCalls { get; } = [];
        public List<Customer> Created { get; } = [];
        public List<Customer> Store { get; } = [];
        public List<CustomerLink> Links { get; } = [];

        public void Seed(Customer customer) => Store.Add(customer);

        public void SeedLink(CustomerLink link) => Links.Add(link);

        public Task UpsertAsync(string ownerUserId, Guid connectionId, string platform, string sender,
            string name, string? username, DateTime seenAt, CancellationToken ct)
        {
            UpsertCalls.Add(new UpsertCall(ownerUserId, connectionId, platform, sender, name, username, seenAt));
            return Task.CompletedTask;
        }

        public Task<(List<Customer> Items, int Total)> ListAsync(
            string ownerUserId, string? search, string? stage, string? channel, int skip, int take, IReadOnlySet<Guid>? connections, CancellationToken ct)
        {
            List<Customer> results = [.. Store.Where(c => c.WorkspaceId == ownerUserId).Skip(skip).Take(take)];
            return Task.FromResult((results, results.Count));
        }

        public Task<Customer?> FindAsync(string ownerUserId, Guid id, CancellationToken ct)
        {
            Customer? customer = Store.FirstOrDefault(c => c.Id == id && c.WorkspaceId == ownerUserId);
            return Task.FromResult(customer);
        }

        public Task<Customer> CreateAsync(Customer customer, CancellationToken ct)
        {
            Store.Add(customer);
            Created.Add(customer);
            return Task.FromResult(customer);
        }

        public Task<Customer?> UpdateStageAsync(string ownerUserId, Guid id, string stage, CancellationToken ct)
        {
            Customer? customer = Store.FirstOrDefault(c => c.Id == id && c.WorkspaceId == ownerUserId);
            if (customer is not null) customer.Stage = stage;
            return Task.FromResult(customer);
        }

        public Task<Customer?> UpdateTagsAsync(string ownerUserId, Guid id, List<string> tags, CancellationToken ct)
        {
            Customer? customer = Store.FirstOrDefault(c => c.Id == id && c.WorkspaceId == ownerUserId);
            if (customer is not null) customer.Tags = tags;
            return Task.FromResult(customer);
        }

        public Task<Customer?> UpdateChannelIdentifierAsync(string ownerUserId, Guid id, string? channelPlatform, string? channelIdentifier, CancellationToken ct)
        {
            Customer? customer = Store.FirstOrDefault(c => c.Id == id && c.WorkspaceId == ownerUserId);
            if (customer is not null)
            {
                customer.ChannelPlatform = channelPlatform;
                customer.ChannelIdentifier = channelIdentifier;
            }
            return Task.FromResult(customer);
        }

        public Task<(Customer Primary, List<CustomerLink> Links)?> GetLinksAsync(string ownerUserId, Guid id, CancellationToken ct)
        {
            Customer? primary = Store.FirstOrDefault(c => c.Id == id && c.WorkspaceId == ownerUserId);
            if (primary is null)
            {
                // Try resolving via a link.
                CustomerLink? asLinked = Links.FirstOrDefault(l => l.LinkedCustomerId == id && l.WorkspaceId == ownerUserId);
                if (asLinked is null) return Task.FromResult<(Customer, List<CustomerLink>)?>(null);
                primary = Store.FirstOrDefault(c => c.Id == asLinked.PrimaryCustomerId && c.WorkspaceId == ownerUserId);
                if (primary is null) return Task.FromResult<(Customer, List<CustomerLink>)?>(null);
            }
            List<CustomerLink> links = [.. Links.Where(l => l.PrimaryCustomerId == primary.Id)];
            return Task.FromResult<(Customer, List<CustomerLink>)?>((primary, links));
        }

        public Task<(Customer Primary, List<CustomerLink> Links)?> FindByIdentityAsync(string ownerUserId, Guid connectionId, string sender, CancellationToken ct)
        {
            CustomerIdentity? identity = Store.SelectMany(c => c.Identities).FirstOrDefault(i => i.ConnectionId == connectionId && i.Sender == sender);
            if (identity is null) return Task.FromResult<(Customer, List<CustomerLink>)?>(null);
            Customer? owner = Store.FirstOrDefault(c => c.Id == identity.CustomerId && c.WorkspaceId == ownerUserId);
            if (owner is null) return Task.FromResult<(Customer, List<CustomerLink>)?>(null);
            CustomerLink? asLinked = Links.FirstOrDefault(l => l.LinkedCustomerId == owner.Id);
            Customer primary = asLinked is null ? owner : Store.FirstOrDefault(c => c.Id == asLinked.PrimaryCustomerId) ?? owner;
            List<CustomerLink> links = [.. Links.Where(l => l.PrimaryCustomerId == primary.Id)];
            return Task.FromResult<(Customer, List<CustomerLink>)?>((primary, links));
        }

        public Task<(Customer Primary, List<CustomerLink> Links)> LinkAsync(string ownerUserId, Guid primaryId, Guid linkedId, CancellationToken ct)
        {
            Customer? primary = Store.FirstOrDefault(c => c.Id == primaryId && c.WorkspaceId == ownerUserId);
            if (primary is null) throw new NotFoundException("Customer not found.");
            Customer? linked = Store.FirstOrDefault(c => c.Id == linkedId && c.WorkspaceId == ownerUserId);
            if (linked is null) throw new NotFoundException("Customer not found.");
            if (Links.Any(l => l.LinkedCustomerId == primaryId)) throw new ConflictException("Customer is already a linked profile.");
            if (Links.Any(l => l.PrimaryCustomerId == linkedId)) throw new ConflictException("Customer already has linked profiles.");
            CustomerLink link = new() { Id = Guid.NewGuid(), WorkspaceId = ownerUserId, PrimaryCustomerId = primaryId, LinkedCustomerId = linkedId, LinkedCustomer = linked };
            Links.Add(link);
            List<CustomerLink> links = [.. Links.Where(l => l.PrimaryCustomerId == primaryId)];
            return Task.FromResult((primary, links));
        }

        public Task<(Customer Primary, List<CustomerLink> Links)> UnlinkAsync(string ownerUserId, Guid primaryId, Guid linkId, CancellationToken ct)
        {
            Customer? primary = Store.FirstOrDefault(c => c.Id == primaryId && c.WorkspaceId == ownerUserId);
            if (primary is null) throw new NotFoundException("Customer not found.");
            CustomerLink? link = Links.FirstOrDefault(l => l.Id == linkId && l.PrimaryCustomerId == primaryId && l.WorkspaceId == ownerUserId);
            if (link is null) throw new NotFoundException("Customer not found.");
            Links.Remove(link);
            List<CustomerLink> remaining = [.. Links.Where(l => l.PrimaryCustomerId == primaryId)];
            return Task.FromResult((primary, remaining));
        }

        public sealed record UpsertCall(string OwnerUserId, Guid ConnectionId, string Platform,
            string Sender, string Name, string? Username, DateTime SeenAt);
    }
}
