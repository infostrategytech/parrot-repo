using Microsoft.Extensions.Options;
using Moq;
using Parrot.Application.Agents;
using Parrot.Application.Contracts.Agents;
using Parrot.Application.Integrations;
using Parrot.Application.Logging;
using Parrot.Domain.Exceptions;

namespace Test.Parrot.Api.Tests.Services.Agents;

// The temporary switch for the live test of agents sharing a number: only the listed workspaces have it, each for itself.
public sealed class MultiAgentSwitchTests
{
    private const string Tested = "workspace-test";
    private const string OtherTested = "workspace-test-2";
    private const string Customer = "workspace-customer";

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

    private MultiAgentSwitch Switch(bool serverAllows = true, string testWorkspaces = $"{Tested}, {OtherTested}") => new(_store,
        Options.Create(new OmniAiSettings { MultiAgentEnabled = serverAllows, MultiAgentTestWorkspaceIds = testWorkspaces }),
        Mock.Of<IAppLogger<MultiAgentSwitch>>());

    [Fact]
    public async Task GetAsync_ForATestWorkspaceNeverSwitched_IsOfferedAndOff()
    {
        Assert.Equal(new MultiAgentSwitchResponse(true, false), await Switch().GetAsync(Tested, default));
    }

    [Fact]
    public async Task SetAsync_TurnsItOnAndOffForThatWorkspaceOnly()
    {
        MultiAgentSwitch multiAgent = Switch();

        Assert.Equal(new MultiAgentSwitchResponse(true, true), await multiAgent.SetAsync(Tested, "user-ngozi", new SetMultiAgentSwitchRequest(true), default));
        Assert.True(await multiAgent.IsOnAsync(Tested, default));
        Assert.False(await multiAgent.IsOnAsync(OtherTested, default));
        Assert.Equal("user-ngozi", _store.Switches[Tested].ChangedBy);

        await multiAgent.SetAsync(Tested, "user-ngozi", new SetMultiAgentSwitchRequest(false), default);
        Assert.False(await multiAgent.IsOnAsync(Tested, default));
    }

    [Fact]
    public async Task SetAsync_ForAWorkspaceNotInTheTest_RefusesAndChangesNothing()
    {
        ValidationException refused = await Assert.ThrowsAsync<ValidationException>(() =>
            Switch().SetAsync(Customer, "user-1", new SetMultiAgentSwitchRequest(true), default));

        Assert.Equal("Agents sharing a number isn't available for this workspace.", refused.Message);
        Assert.Empty(_store.Switches);
    }

    // A workspace taken out of the list, or the server setting turned off, is off at once whatever it saved.
    [Theory]
    [InlineData(false, $"{Tested}")]
    [InlineData(true, "")]
    [InlineData(true, OtherTested)]
    public async Task IsOnAsync_WhenTheServerNoLongerOffersIt_IsOffAndSaysSo(bool serverAllows, string testWorkspaces)
    {
        await Switch().SetAsync(Tested, "user-ngozi", new SetMultiAgentSwitchRequest(true), default);

        MultiAgentSwitch now = Switch(serverAllows, testWorkspaces);

        Assert.False(await now.IsOnAsync(Tested, default));
        Assert.Equal(new MultiAgentSwitchResponse(false, false), await now.GetAsync(Tested, default));
    }

    [Fact]
    public void IsOffered_ReadsTheListLeniently()
    {
        OmniAiSettings settings = new() { MultiAgentEnabled = true, MultiAgentTestWorkspaceIds = " ,WORKSPACE-TEST ,, workspace-test-2 " };

        Assert.True(MultiAgentSwitch.IsOffered(settings, Tested));
        Assert.True(MultiAgentSwitch.IsOffered(settings, OtherTested));
        Assert.False(MultiAgentSwitch.IsOffered(settings, Customer));
        Assert.False(MultiAgentSwitch.IsOffered(settings, ""));
    }
}
