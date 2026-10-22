using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Microsoft.Extensions.DependencyInjection;
using Parrot.Api.Realtime;
using Test.Parrot.Api.Tests.Services.Teams;
using Test.Parrot.Api.Tests.Services.Workspaces;
using StackExchange.Redis;

namespace Test.Parrot.Api.Tests.Services.Realtime;

public class MessagingHubTests
{
    private const string User = "user-1";

    private readonly FakeOnlineSessionStore _sessions = new();
    private readonly FakeWorkspaceRepository _workspaces = new();
    private readonly FakeTeamRepository _team = new();

    public MessagingHubTests() => _workspaces.Seed(User, User);

    private MessagingHub Hub(string connectionId)
    {
        Mock<HubCallerContext> context = new();
        context.SetupGet(c => c.ConnectionId).Returns(connectionId);
        context.SetupGet(c => c.User).Returns(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, User)], "Bearer")));
        context.SetupGet(c => c.ConnectionAborted).Returns(CancellationToken.None);
        RealtimeAudience audience = new(Mock.Of<IServiceScopeFactory>(), NullLogger<RealtimeAudience>.Instance);
        return new MessagingHub(_sessions, TeamFixture.Access(_workspaces, _team), audience, NullLogger<MessagingHub>.Instance)
        {
            Context = context.Object,
        };
    }

    [Fact]
    public async Task Connecting_RegistersTheSession()
    {
        await Hub("conn-1").OnConnectedAsync();

        Assert.Equal(["conn-1"], await _sessions.GetConnectionsAsync(User));
    }

    [Fact]
    public async Task Heartbeat_RenewsTheSession()
    {
        MessagingHub hub = Hub("conn-1");
        await hub.OnConnectedAsync();

        await hub.Heartbeat();

        Assert.Equal(2, _sessions.Registrations);
        Assert.Equal(["conn-1"], await _sessions.GetConnectionsAsync(User));
    }

    [Fact]
    public async Task Disconnecting_RemovesOnlyThatConnection()
    {
        await Hub("laptop").OnConnectedAsync();
        await Hub("phone").OnConnectedAsync();

        await Hub("laptop").OnDisconnectedAsync(null);

        Assert.Equal(["phone"], await _sessions.GetConnectionsAsync(User));
    }

    // Redis was restarted while the client stayed connected: its next
    // heartbeat registers it again, with no other recovery step.
    [Fact]
    public async Task HeartbeatAfterRedisWasEmptied_RegistersAgain()
    {
        MessagingHub hub = Hub("conn-1");
        await hub.OnConnectedAsync();
        _sessions.Clear();

        await hub.Heartbeat();

        Assert.Equal(["conn-1"], await _sessions.GetConnectionsAsync(User));
    }

    // A reconnect gets a new connection id; the old one goes on disconnect or by TTL.
    [Fact]
    public async Task Reconnecting_RegistersTheNewConnection()
    {
        await Hub("old").OnConnectedAsync();
        await Hub("old").OnDisconnectedAsync(new IOException("network dropped"));

        await Hub("new").OnConnectedAsync();

        Assert.Equal(["new"], await _sessions.GetConnectionsAsync(User));
    }

    [Fact]
    public async Task RedisDown_DoesNotRefuseTheConnectionOrFailTheHeartbeatOrDisconnect()
    {
        _sessions.Failure = new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Redis is down");
        MessagingHub hub = Hub("conn-1");

        await hub.OnConnectedAsync();
        await hub.Heartbeat();
        await hub.OnDisconnectedAsync(null);
    }
}
