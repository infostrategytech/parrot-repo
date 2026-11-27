using Microsoft.Extensions.Options;
using Moq;
using Parrot.Application.Agents;
using Parrot.Application.Contracts.Agents;
using Parrot.Application.Integrations;
using Parrot.Application.Logging;
using Parrot.Domain.Exceptions;

namespace Test.Parrot.Api.Tests.Services.Agents;

// The temporary switch for the live test of agents sharing a number: every workspace has it, each for itself, off until turned on.
public sealed class MultiAgentSwitchTests
{
    private const string WorkspaceA = "workspace-a";
    private const string WorkspaceB = "workspace-b";

    private sealed class InMemoryStore : IMultiAgentSwitchStore
    {
        public Dictionary<string, (bool IsOn, string ChangedBy)> Switches { get; } = [];

        public Task<bool> IsOnAsync(string workspaceId, CancellationToken cancellationToken) =>
            Task.FromResult(Switches.TryGetValue(workspaceId, out (bool IsOn, string ChangedBy) saved) && saved.IsOn);

        public Task SetAsync(string workspaceId, bool isOn, string changedBy, CancellationToken cancellationToken)
        {
            Switches[workspaceId] = (isOn, changedBy);
            return Task.CompletedTask;
        }
    }

    private readonly InMemoryStore _store = new();

    private MultiAgentSwitch Switch(bool serverAllows = true) => new(_store,
        Options.Create(new OmniAiSettings { MultiAgentEnabled = serverAllows }), Mock.Of<IAppLogger<MultiAgentSwitch>>());

    // Live needs no setting from DevOps: the switch is there unless the server takes it away.
    [Fact]
    public void Settings_ByDefault_GiveEveryWorkspaceTheSwitch()
    {
        Assert.True(new OmniAiSettings().MultiAgentEnabled);
    }

    [Fact]
    public async Task GetAsync_ForAWorkspaceNeverSwitched_IsOfferedAndOff()
    {
        Assert.Equal(new MultiAgentSwitchResponse(true, false), await Switch().GetAsync(WorkspaceA, default));
    }

    [Fact]
    public async Task SetAsync_TurnsItOnAndOffForThatWorkspaceOnly()
    {
        MultiAgentSwitch multiAgent = Switch();

        Assert.Equal(new MultiAgentSwitchResponse(true, true), await multiAgent.SetAsync(WorkspaceA, "user-ngozi", new SetMultiAgentSwitchRequest(true), default));
        Assert.True(await multiAgent.IsOnAsync(WorkspaceA, default));
        Assert.False(await multiAgent.IsOnAsync(WorkspaceB, default));
        Assert.Equal("user-ngozi", _store.Switches[WorkspaceA].ChangedBy);

        await multiAgent.SetAsync(WorkspaceA, "user-ngozi", new SetMultiAgentSwitchRequest(false), default);
        Assert.False(await multiAgent.IsOnAsync(WorkspaceA, default));
    }

    [Fact]
    public async Task SetAsync_WhenTheServerTakesTheSwitchAway_RefusesAndChangesNothing()
    {
        ValidationException refused = await Assert.ThrowsAsync<ValidationException>(() =>
            Switch(serverAllows: false).SetAsync(WorkspaceA, "user-1", new SetMultiAgentSwitchRequest(true), default));

        Assert.Equal("Agents sharing a number isn't available right now.", refused.Message);
        Assert.Empty(_store.Switches);
    }

    // The server setting turned off is off at once for every workspace, whatever each saved.
    [Fact]
    public async Task IsOnAsync_WhenTheServerTakesTheSwitchAway_IsOffAndSaysSo()
    {
        await Switch().SetAsync(WorkspaceA, "user-ngozi", new SetMultiAgentSwitchRequest(true), default);

        MultiAgentSwitch now = Switch(serverAllows: false);

        Assert.False(await now.IsOnAsync(WorkspaceA, default));
        Assert.Equal(new MultiAgentSwitchResponse(false, false), await now.GetAsync(WorkspaceA, default));
    }
}
