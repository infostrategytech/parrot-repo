using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Parrot.Api.Realtime;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;
using Parrot.Infrastructure.MessageBus;

namespace Test.Parrot.Api.Tests.Services.Realtime;

public class OutboundSettledRouterTests
{
    private static readonly Guid Connection = Guid.Parse("6f1c2d3e-4a5b-4c6d-8e7f-0a1b2c3d4e5f");

    private readonly FakeOnlineSessionStore _sessions = new();
    private readonly Mock<IClientProxy> _proxy = new();
    private readonly Mock<IHubClients> _clients = new();

    public OutboundSettledRouterTests() => _clients.Setup(c => c.Clients(It.IsAny<IReadOnlyList<string>>())).Returns(_proxy.Object);

    private OutboundSettledRouter Router()
    {
        Mock<IHubContext<MessagingHub>> hub = new();
        hub.SetupGet(h => h.Clients).Returns(_clients.Object);
        return new OutboundSettledRouter(_sessions, hub.Object, NullLogger<OutboundSettledRouter>.Instance);
    }

    private static OutboundSettled Settled() => new()
    { MessageId = "outgoing:user-1:1", OwnerUserId = "user-1", ConnectionId = Connection, Customer = "2348012345678", Status = "accepted" };

    [Fact]
    public async Task The_owner_is_told_with_the_message_the_inbox_already_answers_by_reading_the_open_thread()
    {
        await _sessions.RegisterAsync("user-1", "laptop");

        await Router().RouteAsync(Settled(), CancellationToken.None);

        _proxy.Verify(p => p.SendCoreAsync(MessagingHub.ReceiptsChangedMethod,
            It.Is<object?[]>(args => args.Length == 1 && args[0] is ReceiptsChanged && ((ReceiptsChanged)args[0]!).Concerns(Connection, "2348012345678")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task An_offline_owner_is_not_pushed_to_and_a_failed_push_does_not_throw()
    {
        await Router().RouteAsync(Settled(), CancellationToken.None);
        _proxy.Verify(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Never);

        await _sessions.RegisterAsync("user-1", "laptop");
        _proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("gone"));
        await Router().RouteAsync(Settled(), CancellationToken.None);
    }

    [Fact]
    public async Task A_kafka_failure_while_announcing_is_swallowed_because_the_reply_is_already_settled()
    {
        Mock<IMessageBus> bus = new();
        bus.Setup(b => b.PublishAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<OutboundSettled>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Kafka is down"));

        await new KafkaOutboundSettledPublisher(bus.Object, Options.Create(new KafkaSettings()), NullLogger<KafkaOutboundSettledPublisher>.Instance).PublishAsync(Settled());

        bus.Verify(b => b.PublishAsync("messages.outbound.settled", "outgoing:user-1:1", It.IsAny<OutboundSettled>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task The_job_publisher_does_not_swallow_a_failure_so_the_api_can_fail_the_reply()
    {
        Mock<IMessageBus> bus = new();
        bus.Setup(b => b.PublishAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<OutboundJob>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Kafka is down"));

        await Assert.ThrowsAsync<IOException>(() => new KafkaOutboundJobPublisher(bus.Object, Options.Create(new KafkaSettings()))
            .PublishAsync(new OutboundJob { MessageId = "m", ThreadKey = "t", EnqueuedAt = DateTime.UtcNow }));
    }
}
