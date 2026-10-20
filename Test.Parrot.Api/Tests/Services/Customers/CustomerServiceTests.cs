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
        _repository.Seed(new Customer { Id = id, OwnerUserId = Owner, Name = "Test User", Stage = "Customer" });

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
        _repository.Seed(new Customer { Id = id, OwnerUserId = "other-owner", Name = "Theirs", Stage = "Customer" });

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _service.GetAsync(Owner, id, CancellationToken.None));
    }

    // ---- CreateAsync ----

    [Fact]
    public async Task CreateAsync_ValidRequest_CreatesAndReturnsCustomer()
    {
        CreateCustomerRequest request = new("Ada Lovelace", "ada@example.com", null, "New Lead", ["VIP"]);

        CustomerDetailResponse result = await _service.CreateAsync(Owner, request, CancellationToken.None);

        Assert.Equal("Ada Lovelace", result.Name);
        Assert.Equal("ada@example.com", result.Email);
        Assert.Equal("New Lead", result.Stage);
        Assert.Contains("VIP", result.Tags);
        Assert.Single(_repository.Created);
        Assert.Equal(Owner, _repository.Created[0].OwnerUserId);
    }

    [Fact]
    public async Task CreateAsync_NullStage_DefaultsToCustomer()
    {
        CreateCustomerRequest request = new("Buyer", null, null, null, null);

        CustomerDetailResponse result = await _service.CreateAsync(Owner, request, CancellationToken.None);

        Assert.Equal("Customer", result.Stage);
    }

    [Fact]
    public async Task CreateAsync_EmptyName_ThrowsValidationException()
    {
        CreateCustomerRequest request = new("  ", null, null, null, null);

        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.CreateAsync(Owner, request, CancellationToken.None));
    }

    // --------------- fake ---------------

    private sealed class FakeCustomerRepository : ICustomerRepository
    {
        public List<UpsertCall> UpsertCalls { get; } = [];
        public List<Customer> Created { get; } = [];
        private readonly List<Customer> _store = [];

        public void Seed(Customer customer) => _store.Add(customer);

        public Task UpsertAsync(string ownerUserId, Guid connectionId, string platform, string sender,
            string name, string? username, DateTime seenAt, CancellationToken ct)
        {
            UpsertCalls.Add(new UpsertCall(ownerUserId, connectionId, platform, sender, name, username, seenAt));
            return Task.CompletedTask;
        }

        public Task<(List<Customer> Items, int Total)> ListAsync(
            string ownerUserId, string? search, string? stage, string? channel, int skip, int take, CancellationToken ct)
        {
            List<Customer> results = [.. _store.Where(c => c.OwnerUserId == ownerUserId).Skip(skip).Take(take)];
            return Task.FromResult((results, results.Count));
        }

        public Task<Customer?> FindAsync(string ownerUserId, Guid id, CancellationToken ct)
        {
            Customer? customer = _store.FirstOrDefault(c => c.Id == id && c.OwnerUserId == ownerUserId);
            return Task.FromResult(customer);
        }

        public Task<Customer> CreateAsync(Customer customer, CancellationToken ct)
        {
            _store.Add(customer);
            Created.Add(customer);
            return Task.FromResult(customer);
        }

        public sealed record UpsertCall(string OwnerUserId, Guid ConnectionId, string Platform,
            string Sender, string Name, string? Username, DateTime SeenAt);
    }
}
