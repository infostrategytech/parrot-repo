using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Parrot.Api.Realtime;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;
using Parrot.Infrastructure.MessageBus;

namespace Test.Parrot.Api.Tests.Services.Realtime;

public class MediaSettledRouterTests
{
    private const string Owner = "user-1";
    private static readonly Guid Connection = Guid.Parse("6f1c2d3e-4a5b-4c6d-8e7f-0a1b2c3d4e5f");

    private readonly FakeOnlineSessionStore _sessions = new();
    private readonly Mock<IClientProxy> _proxy = new();
    private readonly Mock<IHubClients> _clients = new();
    private IReadOnlyList<string>? _addressed;

    public MediaSettledRouterTests() =>
        _clients.Setup(c => c.Clients(It.IsAny<IReadOnlyList<string>>()))
            .Callback<IReadOnlyList<string>>(ids => _addressed = ids)
            .Returns(_proxy.Object);

    private MediaSettledRouter Router()
    {
        Mock<IHubContext<MessagingHub>> hub = new();
        hub.SetupGet(h => h.Clients).Returns(_clients.Object);
        return new MediaSettledRouter(_sessions, hub.Object, NullLogger<MediaSettledRouter>.Instance);
    }

    private static MediaSettled Settled(string owner = Owner) => new()
    {
        MessageId = "whatsapp:wamid.IMG", OwnerUserId = owner, ConnectionId = Connection, Customer = "2348012345678", Status = "ready",
    };

    private void VerifyPushed(Times times) => _proxy.Verify(p => p.SendCoreAsync(
        MessagingHub.MediaChangedMethod,
        It.Is<object?[]>(args => args.Length == 1 && args[0] is MediaChanged
            && ((MediaChanged)args[0]!).ConnectionId == Connection && ((MediaChanged)args[0]!).Customer == "2348012345678"
            && ((MediaChanged)args[0]!).Status == "ready"),
        It.IsAny<CancellationToken>()), times);

    [Fact]
    public async Task The_owner_is_told_on_every_connection_and_nobody_else()
    {
        await _sessions.RegisterAsync(Owner, "laptop");
        await _sessions.RegisterAsync(Owner, "phone");
        await _sessions.RegisterAsync("another-business", "their-tab");

        await Router().RouteAsync(Settled(), CancellationToken.None);

        VerifyPushed(Times.Once());
        Assert.Equal(["laptop", "phone"], _addressed);
    }

    [Fact]
    public async Task An_offline_owner_is_not_pushed_to()
    {
        await Router().RouteAsync(Settled(), CancellationToken.None);

        VerifyPushed(Times.Never());
    }

    [Fact]
    public async Task A_failed_push_does_not_throw_so_the_event_is_not_redelivered()
    {
        await _sessions.RegisterAsync(Owner, "laptop");
        _proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("gone"));

        await Router().RouteAsync(Settled(), CancellationToken.None);
    }

    [Fact]
    public void A_change_concerns_only_its_own_customers_thread()
    {
        MediaChanged change = new(Connection, "2348012345678", "m", "ready");

        Assert.True(change.Concerns(Connection, "2348012345678"));
        Assert.False(change.Concerns(Connection, "2340000000000"));
        Assert.False(change.Concerns(Guid.NewGuid(), "2348012345678"));
    }

    [Fact]
    public async Task A_kafka_failure_while_announcing_is_swallowed_because_the_file_is_already_settled()
    {
        Mock<IMessageBus> bus = new();
        bus.Setup(b => b.PublishAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<MediaSettled>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Kafka is down"));

        await new KafkaMediaSettledPublisher(bus.Object, Options.Create(new KafkaSettings()), NullLogger<KafkaMediaSettledPublisher>.Instance)
            .PublishAsync(Settled());

        bus.Verify(b => b.PublishAsync("messages.media.settled", "whatsapp:wamid.IMG", It.IsAny<MediaSettled>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
