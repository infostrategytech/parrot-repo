using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Parrot.Api.Realtime;
using Parrot.Application.Teams;
using Parrot.Domain.Enums;
using Test.Parrot.Api.Tests.Services.Workspaces;

namespace Test.Parrot.Api.Tests.Services.Teams;

// A push reaches only the people allowed to see it.
public class RealtimeAudienceTests
{
    private const string Owner = "owner-1";

    private readonly FakeWorkspaceRepository _workspaces = new();
    private readonly FakeTeamRepository _team = new();
    private readonly FakeConnectionDirectory _connections = new();
    private readonly RealtimeAudience _audience;

    public RealtimeAudienceTests()
    {
        _workspaces.Seed("ws-1", Owner);
        ServiceCollection services = new();
        services.AddScoped<IWorkspaceAccessService>(_ => TeamFixture.Access(_workspaces, _team, _connections));
        _audience = new RealtimeAudience(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), NullLogger<RealtimeAudience>.Instance);
    }

    [Fact]
    public async Task AMessage_ReachesTheOwner_AndMembersOnItsChannel_Only()
    {
        Guid whatsApp = _connections.Add("ws-1", MessagingPlatform.WhatsApp);
        _connections.Add("ws-1", MessagingPlatform.Instagram);
        _team.Grant(Owner, "wa-agent", "ws-1", WorkspaceRoles.ConversationManager, [WorkspaceChannels.WhatsApp]);
        _team.Grant(Owner, "ig-agent", "ws-1", WorkspaceRoles.ConversationManager, [WorkspaceChannels.Instagram]);
        _team.Grant(Owner, "analyst", "ws-1", WorkspaceRoles.Analyst);
        _audience.Register("c-owner", Owner, "ws-1");
        _audience.Register("c-wa", "wa-agent", "ws-1");
        _audience.Register("c-ig", "ig-agent", "ws-1");
        _audience.Register("c-analyst", "analyst", "ws-1");

        IReadOnlyList<string> kept = await ((IRealtimeAudience)_audience).ForConversationAsync(
            "ws-1", ["c-owner", "c-wa", "c-ig", "c-analyst", "c-unknown"], whatsApp);

        Assert.Equal(["c-owner", "c-wa"], kept);
    }

    [Fact]
    public async Task AConnectionRegisteredForAnotherWorkspace_IsNotPushedTo()
    {
        _audience.Register("c-owner", Owner, "ws-other");

        Assert.Empty(await _audience.FilterAsync("ws-1", ["c-owner"], _ => true));
    }

    [Fact]
    public async Task ARemovedMember_StopsHearingPushes()
    {
        _team.Grant(Owner, "ada", "ws-1", WorkspaceRoles.ConversationManager);
        _audience.Register("c-ada", "ada", "ws-1");
        _audience.Remove("c-ada");

        Assert.Empty(await _audience.FilterAsync("ws-1", ["c-ada"], _ => true));
    }
}
