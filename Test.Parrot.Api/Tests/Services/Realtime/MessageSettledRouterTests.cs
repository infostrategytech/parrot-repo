using Test.Parrot.Api.Tests.Services.Teams;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Parrot.Api.Realtime;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;
using Parrot.Infrastructure.MessageBus;

namespace Test.Parrot.Api.Tests.Services.Realtime;

public class MessageSettledRouterTests
{
    private const string Owner = "user-1";
    private static readonly Guid Connection = Guid.Parse("6f1c2d3e-4a5b-4c6d-8e7f-0a1b2c3d4e5f");

    private readonly FakeOnlineSessionStore _sessions = new();
    private readonly Mock<IClientProxy> _proxy = new();
    private readonly Mock<IHubClients> _clients = new();
    private IReadOnlyList<string>? _addressed;

    public MessageSettledRouterTests() =>
        _clients.Setup(c => c.Clients(It.IsAny<IReadOnlyList<string>>()))
            .Callback<IReadOnlyList<string>>(ids => _addressed = ids)
            .Returns(_proxy.Object);

    private MessageSettledRouter Router()
    {
        Mock<IHubContext<MessagingHub>> hub = new();
        hub.SetupGet(h => h.Clients).Returns(_clients.Object);
        return new MessageSettledRouter(_sessions, hub.Object, new PassThroughAudience(), NullLogger<MessageSettledRouter>.Instance);
    }

    private static MessageSettled Settled(string kind = SettledKinds.Media, string owner = Owner) => new()
    {
        Kind = kind, MessageId = "whatsapp:wamid.IMG", OwnerUserId = owner, ConnectionId = Connection, Customer = "2348012345678",
        Status = kind == SettledKinds.Media ? "ready" : "accepted",
    };

    [Fact]
    public async Task A_settled_file_is_pushed_to_the_owner_as_mediaChanged_on_every_connection_and_to_nobody_else()
    {
        await _sessions.RegisterAsync(Owner, "laptop");
        await _sessions.RegisterAsync(Owner, "phone");
        await _sessions.RegisterAsync("another-business", "their-tab");

        await Router().RouteAsync(Settled(), CancellationToken.None);

        _proxy.Verify(p => p.SendCoreAsync(MessagingHub.MediaChangedMethod,
            It.Is<object?[]>(args => args.Length == 1 && args[0] is MediaChanged && ((MediaChanged)args[0]!).Concerns(Connection, "2348012345678")
                && ((MediaChanged)args[0]!).Status == "ready"),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(["laptop", "phone"], _addressed);
    }

    [Fact]
    public async Task A_settled_reply_is_pushed_as_the_receiptsChanged_the_inbox_already_answers_by_reading_the_open_thread()
    {
        await _sessions.RegisterAsync(Owner, "laptop");

        await Router().RouteAsync(Settled(SettledKinds.Send), CancellationToken.None);

        _proxy.Verify(p => p.SendCoreAsync(MessagingHub.ReceiptsChangedMethod,
            It.Is<object?[]>(args => args.Length == 1 && args[0] is ReceiptsChanged && ((ReceiptsChanged)args[0]!).Concerns(Connection, "2348012345678")),
            It.IsAny<CancellationToken>()), Times.Once);
        _proxy.Verify(p => p.SendCoreAsync(MessagingHub.MediaChangedMethod, It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task An_offline_owner_is_not_pushed_to_and_a_failed_push_does_not_throw()
    {
        await Router().RouteAsync(Settled(), CancellationToken.None);
        _proxy.Verify(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Never);

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
    public async Task A_kafka_failure_while_announcing_is_swallowed_because_the_work_is_already_settled()
    {
        Mock<IMessageBus> bus = new();
        bus.Setup(b => b.PublishAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<MessageSettled>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Kafka is down"));

        await new KafkaMessageSettledPublisher(bus.Object, Options.Create(new KafkaSettings()), NullLogger<KafkaMessageSettledPublisher>.Instance)
            .PublishAsync(Settled());

        bus.Verify(b => b.PublishAsync("messages.settled", "whatsapp:wamid.IMG", It.IsAny<MessageSettled>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task The_outbound_job_publisher_does_not_swallow_a_failure_so_the_api_can_fail_the_reply()
    {
        Mock<IMessageBus> bus = new();
        bus.Setup(b => b.PublishAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<OutboundJob>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Kafka is down"));

        await Assert.ThrowsAsync<IOException>(() => new KafkaOutboundJobPublisher(bus.Object, Options.Create(new KafkaSettings()))
            .PublishAsync(new OutboundJob { MessageId = "m", ThreadKey = "t", EnqueuedAt = DateTime.UtcNow }));
    }
}
