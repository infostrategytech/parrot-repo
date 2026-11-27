using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Moq;
using Parrot.Api.Controllers;
using Parrot.Api.Media;
using Parrot.Application.Agents;
using Parrot.Application.Contracts.Conversations;
using Parrot.Application.Conversations;
using Parrot.Application.Integrations;
using Parrot.Application.Logging;
using Parrot.Application.Teams;
using Parrot.Application.Workspaces;
using Parrot.Domain.Entities;
using Parrot.Domain.Exceptions;

namespace Test.Parrot.Api.Tests.Controllers;

// The inbox list carries the agents' open questions, for the people who can answer them, so Needs you can show
// every conversation behind the "questions need your answer" count.
public sealed class ConversationsControllerTests
{
    private const string Owner = "workspace-1";

    // The signed-in person's session token, passed on to the AI service's waiting list as it was sent.
    private const string Token = "session-token-1";
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
        _operations.Setup(o => o.OpenByConversationAsync(Owner, Token, It.IsAny<IReadOnlySet<Guid>?>(), It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new ConversationQuestions(WhatsApp, "2348012345678", NeedsAnswer: true,
                new ConversationApproval("apr_1", "payments.refund", "Refund above the limit"), new ConversationAgentRef("agent-nova", "Nova")),
        ]);
    }

    // Live agents by channel, as the orchestrator names them.
    private readonly Dictionary<string, ConversationAgentRef> _channelAgents = [];

    // Live agents switched on for each number, when several agents can share one.
    private readonly Dictionary<Guid, IReadOnlyList<ConversationAgentRef>> _connectionAgents = [];
    // Whether the workspace's switch for agents sharing a number is on.
    private bool _sharesChannels;

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
        orchestrator.Setup(o => o.GetConnectionAgentsAsync(Owner, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_connectionAgents);

        // The workspace's WhatsApp number, so a thread on it can be read.
        Mock<IWhatsAppConnectionRepository> connections = new();
        connections.Setup(c => c.GetByOwnerAndIdAsync(Owner, WhatsApp))
            .ReturnsAsync(new WhatsAppConnection { Id = WhatsApp, WorkspaceId = Owner, Name = "Shop", VerifyTokenEncrypted = "x" });

        Mock<IMultiAgentSwitchStore> switches = new();
        switches.Setup(s => s.IsOnAsync(Owner, It.IsAny<CancellationToken>())).ReturnsAsync(() => _sharesChannels);
        MultiAgentSwitch multiAgent = new(switches.Object,
            Options.Create(new OmniAiSettings { Enabled = true }),
            Mock.Of<IAppLogger<MultiAgentSwitch>>());

        // None of these endpoints send, take notes or read the other channels.
        ConversationsController controller = new(_store.Object, reads.Object, flags.Object, null!, null!,
            new MediaLinks(new EphemeralDataProtectionProvider()), null!, connections.Object, null!, null!, null!,
            takeovers.Object, orchestrator.Object, new MemoryCache(new MemoryCacheOptions()),
            multiAgent,
            _operations.Object, Mock.Of<IAppLogger<ConversationsController>>());
        DefaultHttpContext http = new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(WorkspaceClaims.WorkspaceId, Owner), new Claim(ClaimTypes.NameIdentifier, "user-1")], "test")),
        };
        http.Request.Headers.Authorization = $"Bearer {Token}";
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
    public async Task List_WithSharedChannels_ListsEveryAgentOnTheConversationsNumber()
    {
        _sharesChannels = true;
        ConversationAgentRef nia = new("agt_nia", "Nia", "Ember"), tolu = new("agt_tolu", "Tolu", "Nova"), kora = new("agt_kora", "Kora", "Coral");
        _connectionAgents[WhatsApp] = [nia, tolu, kora];

        List<ConversationSummary> rows = Rows(await Controller(OwnerAccess()).List());

        ConversationSummary bisi = Assert.Single(rows, r => r.Sender == "2348011111111");
        Assert.Equal([nia, tolu, kora], bisi.Agents);
        // A web app from before shared channels still finds one agent there.
        Assert.Equal(nia, bisi.ChannelAgent);
    }

    [Fact]
    public async Task List_WithSharedChannels_ANumberNoAgentAnswersOnHasNone()
    {
        _sharesChannels = true;
        _connectionAgents[Guid.NewGuid()] = [new ConversationAgentRef("agt_tolu", "Tolu")];
        _channelAgents["whatsapp"] = new ConversationAgentRef("agt_tolu", "Tolu");

        List<ConversationSummary> rows = Rows(await Controller(OwnerAccess()).List());

        ConversationSummary bisi = Assert.Single(rows, r => r.Sender == "2348011111111");
        Assert.Empty(bisi.Agents);
        Assert.Null(bisi.ChannelAgent);
    }

    [Fact]
    public async Task List_WithoutSharedChannels_ListsTheChannelsOneAgentAsBefore()
    {
        ConversationAgentRef tolu = new("agt_tolu", "Tolu");
        _channelAgents["whatsapp"] = tolu;

        List<ConversationSummary> rows = Rows(await Controller(OwnerAccess()).List());

        ConversationSummary bisi = Assert.Single(rows, r => r.Sender == "2348011111111");
        Assert.Equal([tolu], bisi.Agents);
        Assert.Equal(tolu, bisi.ChannelAgent);
    }

    [Fact]
    public async Task List_WithSharedChannels_ShowsTheAiServicesNameForTheAgentThatAsked()
    {
        _sharesChannels = true;
        _connectionAgents[Guid.NewGuid()] = [new ConversationAgentRef("AGENT-NOVA", "Nova (live)")];

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

        _operations.Verify(o => o.OpenByConversationAsync(Owner, Token, It.Is<IReadOnlySet<Guid>?>(r => r != null && r.SetEquals(reach)), It.IsAny<CancellationToken>()), Times.Once);
    }

    private ConversationMessage Customer(string id, int minutesAgo) =>
        new(id, "Do you deliver to Ibadan?", false, "received", DateTime.UtcNow.AddMinutes(-minutesAgo));

    private void ThreadHolds(params ConversationMessage[] messages) =>
        _store.Setup(s => s.ReadAsync(Owner, WhatsApp, "2348012345678", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MessagePage(messages, null));

    private static List<ConversationMessage> Thread(IActionResult result) =>
        Assert.IsType<MessagePage>(Assert.IsType<OkObjectResult>(result).Value).Messages.ToList();

    [Fact]
    public async Task Messages_NewestPageForSomeoneWhoCanAnswer_CarriesTheChatsOpenQuestionsInTimeOrder()
    {
        ThreadHolds(Customer("m1", 30), Customer("m2", 5));
        _operations.Setup(o => o.OpenQuestionsAsync(Owner, Token, null, WhatsApp, "2348012345678", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ConversationMessage("esc_1", "Ada wants to know about Ibadan.", false, "open", DateTime.UtcNow.AddMinutes(-20)) { Kind = "ai_question" }]);

        List<ConversationMessage> thread = Thread(await Controller(OwnerAccess()).Messages(WhatsApp, "2348012345678", null, default));

        Assert.Equal(["m1", "esc_1", "m2"], thread.Select(m => m.Id));
        Assert.Equal("ai_question", thread[1].Kind);
    }

    // Switched off, a reply several agents helped write shows as the conversation's one agent, as before.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Messages_AnAiReplysAgents_ShowOnlyWhileTheWorkspaceHasAgentsSharingNumbers(bool isOn)
    {
        _sharesChannels = isOn;
        ThreadHolds(new ConversationMessage("r1", "It left Ikeja.", true, "read", DateTime.UtcNow)
        {
            Kind = "ai", Agents = [new ReplyAgent("agt_tobi", "Tobi", "Nova", "Where is it?", "It left Ikeja.", "answered")],
        });

        ConversationMessage reply = Assert.Single(Thread(await Controller(OwnerAccess()).Messages(WhatsApp, "2348012345678", "cursor", default)));

        Assert.Equal("It left Ikeja.", reply.Text);
        Assert.Equal(isOn ? ["Tobi"] : null, reply.Agents?.Select(a => a.Name));
    }

    [Fact]
    public async Task Messages_OlderPage_DoesNotAskForQuestions()
    {
        ThreadHolds(Customer("m1", 300));

        List<ConversationMessage> thread = Thread(await Controller(OwnerAccess()).Messages(WhatsApp, "2348012345678", "cursor", default));

        Assert.Equal("m1", Assert.Single(thread).Id);
        _operations.Verify(o => o.OpenQuestionsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlySet<Guid>?>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Messages_ForSomeoneWhoCannotTakeOver_HasNoQuestions()
    {
        ThreadHolds(Customer("m1", 30));

        List<ConversationMessage> thread = Thread(await Controller(Teammate(null, WorkspacePermissions.ConversationsView)).Messages(WhatsApp, "2348012345678", null, default));

        Assert.Equal("m1", Assert.Single(thread).Id);
        _operations.Verify(o => o.OpenQuestionsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlySet<Guid>?>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MarkQuestionDone_ClosesItInThisChatAsTheSignedInPersonWithTheirOwnToken()
    {
        IActionResult result = await Controller(OwnerAccess()).MarkQuestionDone(WhatsApp, "wait_1", "2348012345678", default);

        Assert.IsType<NoContentResult>(result);
        _operations.Verify(o => o.CloseQuestionAsync(Owner, Token, null, WhatsApp, "2348012345678", "wait_1", "user-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    // A channel the teammate can't open answers as not found, like one that doesn't exist, and nothing is closed.
    [Fact]
    public async Task MarkQuestionDone_OnAChannelTheTeammateCannotOpen_IsNotFoundAndClosesNothing()
    {
        ConversationsController controller = Controller(Teammate(new HashSet<Guid> { Guid.NewGuid() },
            WorkspacePermissions.ConversationsView, WorkspacePermissions.ConversationsTakeOver));

        await Assert.ThrowsAsync<NotFoundException>(() => controller.MarkQuestionDone(WhatsApp, "wait_1", "2348012345678", default));

        _operations.Verify(o => o.CloseQuestionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlySet<Guid>?>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AnswerQuestion_HandsTheAnswerOnInThisChatWithTheirOwnTokenAndSaysWhetherTheAgentReplies()
    {
        QuestionAnswerRequest request = new("Yes, students get 10% off their first purchase.", SaveForNextTime: true);
        _operations.Setup(o => o.AnswerQuestionAsync(Owner, Token, null, WhatsApp, "2348012345678", "wait_1", request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QuestionAnswerResponse(AgentReplies: true));

        IActionResult result = await Controller(OwnerAccess()).AnswerQuestion(WhatsApp, "wait_1", "2348012345678", request, default);

        Assert.Equal(new QuestionAnswerResponse(AgentReplies: true), Assert.IsType<OkObjectResult>(result).Value);
    }

    // A channel the teammate can't open answers as not found, like one that doesn't exist, and nothing reaches the agent.
    [Fact]
    public async Task AnswerQuestion_OnAChannelTheTeammateCannotOpen_IsNotFoundAndSendsNothing()
    {
        ConversationsController controller = Controller(Teammate(new HashSet<Guid> { Guid.NewGuid() },
            WorkspacePermissions.ConversationsView, WorkspacePermissions.ConversationsTakeOver));

        await Assert.ThrowsAsync<NotFoundException>(() =>
            controller.AnswerQuestion(WhatsApp, "wait_1", "2348012345678", new QuestionAnswerRequest("Yes", SaveForNextTime: true), default));

        _operations.Verify(o => o.AnswerQuestionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlySet<Guid>?>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<QuestionAnswerRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Saving changes what the agents know for every customer, so only people who may change knowledge can ask for it.
    [Fact]
    public async Task AnswerQuestion_SavingForNextTimeWithoutKnowledgeAccess_IsForbiddenAndSendsNothing()
    {
        ConversationsController controller = Controller(Teammate(null, WorkspacePermissions.ConversationsView, WorkspacePermissions.ConversationsTakeOver));

        ForbiddenException error = await Assert.ThrowsAsync<ForbiddenException>(() =>
            controller.AnswerQuestion(WhatsApp, "wait_1", "2348012345678", new QuestionAnswerRequest("Yes", SaveForNextTime: true), default));

        Assert.Contains("Untick Save for next time", error.Message);
        _operations.Verify(o => o.AnswerQuestionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlySet<Guid>?>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<QuestionAnswerRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AnswerQuestion_WithoutSavingForNextTime_NeedsNoKnowledgeAccess()
    {
        QuestionAnswerRequest request = new("Yes", SaveForNextTime: false);
        _operations.Setup(o => o.AnswerQuestionAsync(Owner, Token, null, WhatsApp, "2348012345678", "wait_1", request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QuestionAnswerResponse(AgentReplies: true));
        ConversationsController controller = Controller(Teammate(null, WorkspacePermissions.ConversationsView, WorkspacePermissions.ConversationsTakeOver));

        IActionResult result = await controller.AnswerQuestion(WhatsApp, "wait_1", "2348012345678", request, default);

        Assert.IsType<OkObjectResult>(result);
    }

    [Theory]
    [InlineData(WorkspacePermissions.AgentsCreate)]
    [InlineData(WorkspacePermissions.AgentsEdit)]
    [InlineData(WorkspacePermissions.WorkspaceManage)]
    public async Task AnswerQuestion_SavingForNextTimeWithKnowledgeAccess_HandsItOn(string permission)
    {
        QuestionAnswerRequest request = new("Yes", SaveForNextTime: true);
        _operations.Setup(o => o.AnswerQuestionAsync(Owner, Token, null, WhatsApp, "2348012345678", "wait_1", request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QuestionAnswerResponse(AgentReplies: true));
        ConversationsController controller = Controller(Teammate(null, WorkspacePermissions.ConversationsView, WorkspacePermissions.ConversationsTakeOver, permission));

        IActionResult result = await controller.AnswerQuestion(WhatsApp, "wait_1", "2348012345678", request, default);

        Assert.IsType<OkObjectResult>(result);
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
