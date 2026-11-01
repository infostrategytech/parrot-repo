using Test.Parrot.Api.Tests.Services.Teams;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Parrot.Api.Realtime;
using Parrot.Application.Messaging;

namespace Test.Parrot.Api.Tests.Services.Realtime;

public class HubReceiptNotifierTests
{
    private const string Owner = "user-1";
    private static readonly Guid Connection = Guid.NewGuid();

    private readonly FakeOnlineSessionStore _sessions = new();
    private readonly Mock<IClientProxy> _proxy = new();
    private readonly Mock<IHubClients> _clients = new();
    private IReadOnlyList<string>? _addressed;

    public HubReceiptNotifierTests() =>
        _clients.Setup(c => c.Clients(It.IsAny<IReadOnlyList<string>>()))
            .Callback<IReadOnlyList<string>>(ids => _addressed = ids)
            .Returns(_proxy.Object);

    private HubReceiptNotifier Notifier()
    {
        Mock<IHubContext<MessagingHub>> hub = new();
        hub.SetupGet(h => h.Clients).Returns(_clients.Object);
        return new HubReceiptNotifier(_sessions, hub.Object, new PassThroughAudience(), NullLogger<HubReceiptNotifier>.Instance);
    }

    private void VerifyPushed(Times times) => _proxy.Verify(p => p.SendCoreAsync(
        MessagingHub.ReceiptsChangedMethod,
        It.Is<object?[]>(args => args.Length == 1 && ((ReceiptsChanged)args[0]!).ConnectionId == Connection && ((ReceiptsChanged)args[0]!).Customer == "2348012345678"),
        It.IsAny<CancellationToken>()), times);

    [Fact]
    public async Task TheOwnersOpenInboxesAreToldWhichConversationChanged()
    {
        await _sessions.RegisterAsync(Owner, "laptop");
        await _sessions.RegisterAsync(Owner, "phone");

        await Notifier().ReceiptsChangedAsync(Owner, Connection, "2348012345678", default);

        VerifyPushed(Times.Once());
        Assert.Equal(["laptop", "phone"], _addressed);
    }

    [Fact]
    public async Task AnotherUsersInboxIsNotTold()
    {
        await _sessions.RegisterAsync("someone-else", "laptop");

        await Notifier().ReceiptsChangedAsync(Owner, Connection, "2348012345678", default);

        VerifyPushed(Times.Never());
    }

    [Fact]
    public async Task WhenRedisIsDownNothingThrowsAndTheInboxCatchesUpOnItsNextPoll()
    {
        _sessions.Failure = new InvalidOperationException("redis down");

        await Notifier().ReceiptsChangedAsync(Owner, Connection, "2348012345678", default);

        VerifyPushed(Times.Never());
    }

    [Fact]
    public async Task AFailedPushNeverThrows()
    {
        await _sessions.RegisterAsync(Owner, "laptop");
        _proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("connection gone"));

        await Notifier().ReceiptsChangedAsync(Owner, Connection, "2348012345678", default);
    }

    [Theory]
    [InlineData("2348012345678", "2348012345678", true)]
    [InlineData("2348012345678", "2349011112222", false)]
    [InlineData(null, "2349011112222", true)]
    public void AChangeConcernsTheThreadItNamesOrAnyOnItsConnectionWhenNoCustomerIsNamed(string? customer, string openThread, bool concerns) =>
        Assert.Equal(concerns, new ReceiptsChanged(Connection, customer).Concerns(Connection, openThread));

    [Fact]
    public void AChangeOnAnotherConnectionConcernsNoThreadHere() =>
        Assert.False(new ReceiptsChanged(Connection, null).Concerns(Guid.NewGuid(), "2348012345678"));
}
