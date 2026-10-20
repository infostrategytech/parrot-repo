using Parrot.Application.Customers;
using Parrot.Domain.Enums;

namespace Test.Parrot.Api.Tests.Services.Customers;

/// <summary>
/// Unit tests for CustomerService.UpsertFromMessageAsync. The repository is a
/// simple in-memory fake — no Postgres or Moq required here.
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

    [Fact]
    public async Task UpsertFromMessageAsync_NewSender_CreatesOneCustomer()
    {
        await _service.UpsertFromMessageAsync(Owner, MessagingPlatform.WhatsApp, ConnectionId,
            "+2348012345678", "Amaka Obi", null, DateTime.UtcNow, CancellationToken.None);

        Assert.Single(_repository.Calls);
        FakeCustomerRepository.Call call = _repository.Calls[0];
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

        Assert.Equal("1234567890", _repository.Calls[0].Name);
    }

    [Fact]
    public async Task UpsertFromMessageAsync_EmptyName_FallsBackToSender()
    {
        await _service.UpsertFromMessageAsync(Owner, MessagingPlatform.Instagram, ConnectionId,
            "igsid_abc", "", "@handle", DateTime.UtcNow, CancellationToken.None);

        Assert.Equal("igsid_abc", _repository.Calls[0].Name);
        Assert.Equal("@handle", _repository.Calls[0].Username);
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

        Assert.Equal(2, _repository.Calls.Count);
    }

    [Fact]
    public async Task UpsertFromMessageAsync_PlatformIsLowercased()
    {
        await _service.UpsertFromMessageAsync(Owner, MessagingPlatform.TikTok, ConnectionId,
            "tiktok_id", "Creator", "handle", DateTime.UtcNow, CancellationToken.None);

        Assert.Equal("tiktok", _repository.Calls[0].Platform);
    }

    // --------------- fake ---------------

    private sealed class FakeCustomerRepository : ICustomerRepository
    {
        public List<Call> Calls { get; } = [];

        public Task UpsertAsync(string ownerUserId, Guid connectionId, string platform, string sender,
            string name, string? username, DateTime seenAt, CancellationToken ct)
        {
            Calls.Add(new Call(ownerUserId, connectionId, platform, sender, name, username, seenAt));
            return Task.CompletedTask;
        }

        public sealed record Call(string OwnerUserId, Guid ConnectionId, string Platform,
            string Sender, string Name, string? Username, DateTime SeenAt);
    }
}
