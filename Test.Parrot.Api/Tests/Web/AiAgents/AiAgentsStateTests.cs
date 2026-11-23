using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Parrot.Web.Components.Pages.Dashboard.AiAgents;
using Parrot.Web.Features.Auth.State;
using Parrot.Web.Features.Dashboard.AiAgents;
using Parrot.Web.Features.Dashboard.AiAgents.State;
using Parrot.Web.Services.Realtime;

namespace Test.Parrot.Api.Tests.Web.AiAgents;

// The Agents page, an agent's page and the Overview show the waiting questions from the operations feed. Questions
// answered in the inbox are reported by no agent, so each visit has to read them again.
public sealed class AiAgentsStateTests
{
    private readonly Mock<IAgentStore> _store = new();
    private readonly Mock<IAiOperationsFeed> _feed = new();

    private AiAgentsState State() =>
        new(_store.Object, _feed.Object, new RealtimeClient(new AuthState(), new ConfigurationBuilder().Build(), NullLogger<RealtimeClient>.Instance));

    [Fact]
    public async Task LoadAsync_OnALaterVisit_ReadsTheWaitingQuestionsAgainWithoutListingTheAgentsAgain()
    {
        _store.Setup(s => s.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([new AiAgent { Id = "agent-tolu", Name = "Tolu" }]);
        AiAgentsState state = State();
        await state.LoadAsync();

        await state.LoadAsync();

        _store.Verify(s => s.ListAsync(It.IsAny<CancellationToken>()), Times.Once);
        _feed.Verify(f => f.RefreshAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // The pages keep the questions reachable when the agents can't be listed (the AI service is down, say).
    [Fact]
    public async Task LoadAsync_WhenTheAgentsCannotBeListed_StillReadsTheWaitingQuestions()
    {
        _store.Setup(s => s.ListAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("down"));
        AiAgentsState state = State();

        await state.LoadAsync();

        Assert.True(state.LoadFailed);
        _feed.Verify(f => f.RefreshAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
