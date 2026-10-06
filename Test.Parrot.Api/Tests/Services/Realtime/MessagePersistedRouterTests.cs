using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Parrot.Api.Realtime;
using Parrot.Application.Messaging;
using Parrot.Domain.Enums;
using StackExchange.Redis;

namespace Test.Parrot.Api.Tests.Services.Realtime;

public class MessagePersistedRouterTests
{
    private const string Owner = "user-1";

    private readonly FakeOnlineSessionStore _sessions = new();
    private readonly Mock<IClientProxy> _proxy = new();
    private readonly Mock<IHubClients> _clients = new();
    private IReadOnlyList<string>? _addressed;

    public MessagePersistedRouterTests()
    {
        _clients.Setup(c => c.Clients(It.IsAny<IReadOnlyList<string>>()))
            .Callback<IReadOnlyList<string>>(ids => _addressed = ids)
            .Returns(_proxy.Object);
    }

    private MessagePersistedRouter Router()
    {
        Mock<IHubContext<MessagingHub>> hub = new();
        hub.SetupGet(h => h.Clients).Returns(_clients.Object);
        return new MessagePersistedRouter(_sessions, hub.Object, NullLogger<MessagePersistedRouter>.Instance);
    }

    private static MessagePersisted Persisted(string? owner = Owner) => new()
    {
        Source = MessagingPlatform.WhatsApp,
        CorrelationId = "4bf92f3577b34da6a3ce929d0e0e4736",
        Data = new StoredMessage
        {
            MessageId = "whatsapp:wamid.1",
            ConversationKey = "whatsapp:2348012345678:2349011112222",
            Sequence = 42,
            Platform = MessagingPlatform.WhatsApp,
            OwnerUserId = owner,
            Outgoing = false,
            Sender = "2348012345678",
            Receiver = "2349011112222",
            Type = "text",
            Text = "Hello",
            PersistedAt = DateTime.UtcNow,
        },
    };

    private void VerifyPushed(Times times) => _proxy.Verify(p => p.SendCoreAsync(
        MessagingHub.MessagePersistedMethod,
        It.Is<object?[]>(args => args.Length == 1 && ((StoredMessage)args[0]!).MessageId == "whatsapp:wamid.1"),
        It.IsAny<CancellationToken>()), times);

    [Fact]
    public async Task OnlineRecipient_GetsTheMessageOnEveryConnection()
    {
        await _sessions.RegisterAsync(Owner, "laptop");
        await _sessions.RegisterAsync(Owner, "phone");

        await Router().RouteAsync(Persisted(), CancellationToken.None);

        VerifyPushed(Times.Once());
        Assert.Equal(["laptop", "phone"], _addressed);
    }

    [Fact]
    public async Task OfflineRecipient_GetsNothingAndNothingIsWrittenToRedis()
    {
        await Router().RouteAsync(Persisted(), CancellationToken.None);

        VerifyPushed(Times.Never());
        Assert.Equal(0, _sessions.Registrations);
        Assert.Empty(await _sessions.GetConnectionsAsync(Owner));
    }

    [Fact]
    public async Task OnlyTheOwnersConnectionsAreAddressed()
    {
        await _sessions.RegisterAsync(Owner, "owner-tab");
        await _sessions.RegisterAsync("another-business", "their-tab");

        await Router().RouteAsync(Persisted(), CancellationToken.None);

        Assert.Equal(["owner-tab"], _addressed);
    }

    [Fact]
    public async Task AMessageWithNoOwnerIsSkipped()
    {
        await Router().RouteAsync(Persisted(owner: null), CancellationToken.None);

        VerifyPushed(Times.Never());
    }

    // The message is already in MongoDB; Redis being down only costs the live push.
    [Fact]
    public async Task RedisDown_DoesNotThrow()
    {
        _sessions.Failure = new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Redis is down");

        await Router().RouteAsync(Persisted(), CancellationToken.None);

        VerifyPushed(Times.Never());
    }

    [Fact]
    public async Task SignalRFailure_DoesNotThrow()
    {
        await _sessions.RegisterAsync(Owner, "conn-1");
        _proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("connection reset"));

        await Router().RouteAsync(Persisted(), CancellationToken.None);
    }

    // The event can arrive twice; pushing it twice is fine because the client
    // keeps one copy per messageId.
    [Fact]
    public async Task ARedeliveredEventIsPushedAgain()
    {
        await _sessions.RegisterAsync(Owner, "conn-1");

        await Router().RouteAsync(Persisted(), CancellationToken.None);
        await Router().RouteAsync(Persisted(), CancellationToken.None);

        VerifyPushed(Times.Exactly(2));
    }

    // Shutdown still propagates so the bus can stop cleanly.
    [Fact]
    public async Task Cancellation_IsNotSwallowed()
    {
        await _sessions.RegisterAsync(Owner, "conn-1");
        _proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() => Router().RouteAsync(Persisted(), CancellationToken.None));
    }
}
