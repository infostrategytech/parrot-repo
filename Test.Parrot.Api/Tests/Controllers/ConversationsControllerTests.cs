using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using Parrot.Api.Controllers;
using Parrot.Application.Agents;
using Parrot.Application.Contracts.Conversations;
using Parrot.Application.Conversations;
using Parrot.Application.Logging;
using Parrot.Application.Teams;
using Parrot.Application.Workspaces;
using Parrot.Domain.Entities;

namespace Test.Parrot.Api.Tests.Controllers;

// The inbox list carries the agents' open questions, for the people who can answer them, so Needs you can show
// every conversation behind the "questions need your answer" count.
public sealed class ConversationsControllerTests
{
    private const string Owner = "workspace-1";
    private static readonly Guid WhatsApp = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly Mock<IConversationStore> _store = new();
    private readonly Mock<IAiOperationsService> _operations = new();
    private readonly ConversationSummary _recent = new(WhatsApp, "2348011111111", "Bisi", "Thanks!", DateTime.UtcNow);
    private readonly ConversationSummary _older = new(WhatsApp, "2348012345678", "Ada", "Do you deliver to Ibadan?", DateTime.UtcNow.AddDays(-3));

    public ConversationsControllerTests()
    {
        _store.Setup(s => s.ListAsync(Owner, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([_recent]);
        _store.Setup(s => s.ListAsync(Owner, It.IsAny<IReadOnlySet<Guid>>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([_recent]);
        _store.Setup(s => s.FindAsync(Owner, It.IsAny<IReadOnlyCollection<(Guid, string)>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, IReadOnlyCollection<(Guid Connection, string Sender)> wanted, CancellationToken _) =>
                wanted.Contains((_older.ConnectionId, _older.Sender)) ? [_older] : []);
        _operations.Setup(o => o.OpenByConversationAsync(Owner, It.IsAny<IReadOnlySet<Guid>?>(), It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new ConversationQuestions(WhatsApp, "2348012345678", NeedsAnswer: true,
                new ConversationApproval("apr_1", "payments.refund", "Refund above the limit"), new ConversationAgentRef("agent-nova", "Nova")),
        ]);
    }

    // Live agents by channel, as the orchestrator names them.
    private readonly Dictionary<string, ConversationAgentRef> _channelAgents = [];

    private ConversationsController Controller(WorkspaceAccess access)
    {
        Mock<IConversationReadStore> reads = new();
        reads.Setup(r => r.CountUnreadAsync(Owner, It.IsAny<string>(), It.IsAny<IReadOnlyCollection<(Guid, string)>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<(Guid, string), int>());
        Mock<IConversationFlagStore> flags = new();
        flags.Setup(f => f.ListAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<(Guid Connection, string Sender), ConversationFlags>());
        Mock<IHumanTakeoverStore> takeovers = new();
        takeovers.Setup(t => t.GetActiveAsync(Owner, It.IsAny<IReadOnlyList<(Guid Connection, string Sender)>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<(Guid, string), DateTime?>());
        Mock<IAgentOrchestratorClient> orchestrator = new();
        orchestrator.Setup(o => o.GetChannelAgentMapAsync(Owner, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_channelAgents);

        // The list reads none of the send, media or connection services.
        ConversationsController controller = new(_store.Object, reads.Object, flags.Object, null!, null!, null!, null!,
            null!, null!, null!, null!, takeovers.Object, orchestrator.Object, new MemoryCache(new MemoryCacheOptions()),
            _operations.Object, Mock.Of<IAppLogger<ConversationsController>>());
        DefaultHttpContext http = new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(WorkspaceClaims.WorkspaceId, Owner), new Claim(ClaimTypes.NameIdentifier, "user-1")], "test")),
        };
        http.Items[typeof(WorkspaceAccess)] = access;
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private static WorkspaceAccess OwnerAccess() => WorkspaceAccess.ForOwner(new Workspace { Id = Owner, OwnerId = "user-1" });

    private static WorkspaceAccess Teammate(IReadOnlySet<Guid>? connections, params string[] permissions) =>
        new(new Workspace { Id = Owner, OwnerId = "user-0" }, "Member", permissions.ToHashSet(), new HashSet<string>(), connections);

    private static List<ConversationSummary> Rows(IActionResult result) =>
        Assert.IsAssignableFrom<IEnumerable<ConversationSummary>>(Assert.IsType<OkObjectResult>(result).Value).ToList();

    [Fact]
    public async Task List_ForSomeoneWhoCanAnswer_FlagsTheOpenQuestionAndBringsItsOlderConversationOntoTheFirstPage()
    {
        List<ConversationSummary> rows = Rows(await Controller(OwnerAccess()).List());

        Assert.Equal(2, rows.Count);
        ConversationSummary ada = Assert.Single(rows, r => r.Sender == "2348012345678");
        Assert.True(ada.NeedsAnswer);
        Assert.Equal("apr_1", ada.PendingApproval?.ApprovalId);
        Assert.Equal(new ConversationAgentRef("agent-nova", "Nova"), ada.AiAgent);
        ConversationSummary bisi = Assert.Single(rows, r => r.Sender == "2348011111111");
        Assert.False(bisi.NeedsAnswer);
        Assert.Null(bisi.PendingApproval);
        Assert.Null(bisi.AiAgent);
    }

    // Parrot's own agent list may not know an agent the orchestrator keeps; the orchestrator's name for it is the one shown.
    [Fact]
    public async Task List_WhenTheAgentThatAskedIsLive_ShowsTheOrchestratorsNameForIt()
    {
        _channelAgents["instagram"] = new ConversationAgentRef("AGENT-NOVA", "Nova (live)");

        List<ConversationSummary> rows = Rows(await Controller(OwnerAccess()).List());

        Assert.Equal("Nova (live)", Assert.Single(rows, r => r.Sender == "2348012345678").AiAgent?.Name);
    }

    [Fact]
    public async Task List_ForSomeoneWhoCannotTakeOver_SendsNoQuestionsAndNeverReadsThem()
    {
        List<ConversationSummary> rows = Rows(await Controller(Teammate(null, WorkspacePermissions.ConversationsView)).List());

        ConversationSummary bisi = Assert.Single(rows);
        Assert.False(bisi.NeedsAnswer);
        _operations.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task List_ForATeammateLimitedToSomeChannels_AsksOnlyForQuestionsOnTheirChannels()
    {
        HashSet<Guid> reach = [WhatsApp];

        await Controller(Teammate(reach, WorkspacePermissions.ConversationsView, WorkspacePermissions.ConversationsTakeOver)).List();

        _operations.Verify(o => o.OpenByConversationAsync(Owner, It.Is<IReadOnlySet<Guid>?>(r => r != null && r.SetEquals(reach)), It.IsAny<CancellationToken>()), Times.Once);
    }

    // Later pages are newest-first as they are; the first page already brought the question's conversation in.
    [Fact]
    public async Task List_OnALaterPage_DoesNotAddTheQuestionsConversationAgain()
    {
        List<ConversationSummary> rows = Rows(await Controller(OwnerAccess()).List(skip: 50));

        Assert.Equal("2348011111111", Assert.Single(rows).Sender);
        _store.Verify(s => s.FindAsync(Owner, It.IsAny<IReadOnlyCollection<(Guid, string)>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
