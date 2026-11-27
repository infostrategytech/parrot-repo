using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Parrot.Application.Agents;
using Parrot.Application.Conversations;
using Parrot.Application.Integrations;
using Parrot.Domain.Exceptions;
using Parrot.Infrastructure.Agents;

namespace Test.Parrot.Api.Tests.Services.Agents;

public class OmniAiAgentChannelLookupTests
{
    private const string Workspace = "workspace-1";
    private static readonly Guid Answered = Guid.NewGuid();
    private static readonly Guid Unanswered = Guid.NewGuid();
    private static readonly ConversationAgentRef Tolu = new("agt_1", "Tolu");

    private readonly Mock<IAgentOrchestratorClient> _orchestrator = new();

    [Fact]
    public async Task IsAgentLiveAsync_WithSharedChannels_AnswersForTheNumberTheMessageCameIn()
    {
        _orchestrator.Setup(o => o.GetConnectionAgentsAsync(Workspace, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, IReadOnlyList<ConversationAgentRef>> { [Answered] = [Tolu] });
        OmniAiAgentChannelLookup lookup = Lookup(sharesChannels: true);

        Assert.True(await lookup.IsAgentLiveAsync(Workspace, "whatsapp", Answered, CancellationToken.None));
        // Another WhatsApp number of the same workspace, with no agent on it.
        Assert.False(await lookup.IsAgentLiveAsync(Workspace, "whatsapp", Unanswered, CancellationToken.None));
        _orchestrator.Verify(o => o.GetChannelAgentMapAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task IsAgentLiveAsync_WithoutSharedChannels_AnswersForTheChannelKindAsBefore()
    {
        _orchestrator.Setup(o => o.GetChannelAgentMapAsync(Workspace, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, ConversationAgentRef> { ["whatsapp"] = Tolu });
        OmniAiAgentChannelLookup lookup = Lookup(sharesChannels: false);

        Assert.True(await lookup.IsAgentLiveAsync(Workspace, "whatsapp", Unanswered, CancellationToken.None));
        Assert.False(await lookup.IsAgentLiveAsync(Workspace, "instagram", Unanswered, CancellationToken.None));
        _orchestrator.Verify(o => o.GetConnectionAgentsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task IsAgentLiveAsync_WithSharedChannelsAndNoNumber_AnswersForTheChannelKind()
    {
        _orchestrator.Setup(o => o.GetChannelAgentMapAsync(Workspace, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, ConversationAgentRef> { ["whatsapp"] = Tolu });
        OmniAiAgentChannelLookup lookup = Lookup(sharesChannels: true);

        Assert.True(await lookup.IsAgentLiveAsync(Workspace, "whatsapp", null, CancellationToken.None));
    }

    [Fact]
    public async Task IsAgentLiveAsync_WhenTheAiServiceCantBeReached_SendsTheMessageToTheTeam()
    {
        _orchestrator.Setup(o => o.GetConnectionAgentsAsync(Workspace, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ServiceUnavailableException("The AI service could not be reached.", new HttpRequestException()));
        OmniAiAgentChannelLookup lookup = Lookup(sharesChannels: true);

        Assert.False(await lookup.IsAgentLiveAsync(Workspace, "whatsapp", Answered, CancellationToken.None));
    }

    [Fact]
    public async Task IsAgentLiveAsync_WithSharedChannels_AsksTheAiServiceOncePerWorkspaceWhileFresh()
    {
        _orchestrator.Setup(o => o.GetConnectionAgentsAsync(Workspace, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, IReadOnlyList<ConversationAgentRef>> { [Answered] = [Tolu] });
        OmniAiAgentChannelLookup lookup = Lookup(sharesChannels: true);

        await lookup.IsAgentLiveAsync(Workspace, "whatsapp", Answered, CancellationToken.None);
        await lookup.IsAgentLiveAsync(Workspace, "whatsapp", Unanswered, CancellationToken.None);

        _orchestrator.Verify(o => o.GetConnectionAgentsAsync(Workspace, It.IsAny<CancellationToken>()), Times.Once);
    }

    // Only workspaces in the live test have the switch; another's is never read.
    [Fact]
    public async Task IsAgentLiveAsync_ForAWorkspaceNotInTheTest_AnswersForTheChannelKindWhateverItsSwitchSays()
    {
        _orchestrator.Setup(o => o.GetChannelAgentMapAsync(Workspace, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, ConversationAgentRef> { ["whatsapp"] = Tolu });
        _switches.Setup(s => s.IsOnAsync(Workspace, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        OmniAiAgentChannelLookup lookup = Lookup(sharesChannels: true, testWorkspaces: "workspace-2");

        Assert.True(await lookup.IsAgentLiveAsync(Workspace, "whatsapp", Unanswered, CancellationToken.None));
        _orchestrator.Verify(o => o.GetConnectionAgentsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _switches.Verify(s => s.IsOnAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // A switch that can't be read is off: messages still route as with one agent per number.
    [Fact]
    public async Task IsAgentLiveAsync_WhenTheSwitchCantBeRead_AnswersForTheChannelKind()
    {
        _orchestrator.Setup(o => o.GetChannelAgentMapAsync(Workspace, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, ConversationAgentRef> { ["whatsapp"] = Tolu });
        OmniAiAgentChannelLookup lookup = Lookup(sharesChannels: true);
        _switches.Setup(s => s.IsOnAsync(Workspace, It.IsAny<CancellationToken>())).ThrowsAsync(new TimeoutException("MongoDB did not answer."));

        Assert.True(await lookup.IsAgentLiveAsync(Workspace, "whatsapp", Unanswered, CancellationToken.None));
        _orchestrator.Verify(o => o.GetConnectionAgentsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private readonly Mock<IMultiAgentSwitchStore> _switches = new();

    // The workspace is in the live test; sharesChannels is whether its switch is on.
    private OmniAiAgentChannelLookup Lookup(bool sharesChannels, string testWorkspaces = Workspace)
    {
        _switches.Setup(s => s.IsOnAsync(Workspace, It.IsAny<CancellationToken>())).ReturnsAsync(sharesChannels);
        return new OmniAiAgentChannelLookup(
            _orchestrator.Object,
            _switches.Object,
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new OmniAiSettings { Enabled = true, MultiAgentEnabled = true, MultiAgentTestWorkspaceIds = testWorkspaces }),
            NullLogger<OmniAiAgentChannelLookup>.Instance);
    }
}
