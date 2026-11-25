using Microsoft.Extensions.Caching.Memory;
using Moq;
using Parrot.Application.Agents;
using Parrot.Application.Contracts.Conversations;
using Parrot.Application.Conversations;
using Parrot.Application.Logging;
using Parrot.Domain.Entities;
using Parrot.Domain.Exceptions;

namespace Test.Parrot.Api.Tests.Services.Agents;

public class AiOperationsServiceTests
{
    private const string Owner = "owner-1";
    private const string Nova = "11111111-1111-1111-1111-111111111111";
    private const string Atlas = "22222222-2222-2222-2222-222222222222";

    // Ada's chat: the orchestrator names it by its key, the inbox by its connection and customer.
    private const string AdaChat = "whatsapp:2348012345678:2349011112222";
    private static readonly Guid WhatsApp = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Instagram = Guid.Parse("44444444-4444-4444-4444-444444444444");

    // Mid-afternoon on the 8th, UTC: "today" is the 8th, "yesterday" the 7th.
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTime Today = Now.UtcDateTime.Date;

    // Other chats in the same workspace, on the same WhatsApp number unless said otherwise.
    private const string BolaChat = "whatsapp:2348022222222:2349011112222";
    private const string KunleChat = "whatsapp:2348033333333:2349011112222";
    private const string TayoChat = "whatsapp:2348044444444:2349011112222";
    private const string BayoChat = "instagram:17841400000000001:9876543210";

    // The signed-in person's session token, as the AI service reads them from it.
    private const string Token = "session-ngozi";

    private readonly FakeAgentActivityStore _activity = new();
    private readonly FakeWaitingListClient _waiting = new() { People = { [Token] = "user-ngozi" } };
    private readonly Mock<IAgentRepository> _agents = new();
    private readonly Mock<IAgentOrchestratorClient> _orchestrator = new();
    private readonly Mock<IConversationStore> _conversations = new();
    // Nobody has taken over a chat unless a test says so.
    private readonly Mock<IHumanTakeoverStore> _takeovers = new();
    private readonly Dictionary<string, (Guid Connection, string Sender)> _inbox = new()
    {
        [AdaChat] = (WhatsApp, "2348012345678"),
        [BolaChat] = (WhatsApp, "2348022222222"),
        [KunleChat] = (WhatsApp, "2348033333333"),
        [TayoChat] = (WhatsApp, "2348044444444"),
        [BayoChat] = (Instagram, "9876543210"),
    };
    private readonly AiOperationsService _sut;

    public AiOperationsServiceTests()
    {
        _agents.Setup(a => a.GetByWorkspaceAsync(Owner)).ReturnsAsync(
        [
            new Agent { Id = Guid.Parse(Nova), WorkspaceId = Owner, Name = "Nova", Description = "", MascotUrl = "x" },
            new Agent { Id = Guid.Parse(Atlas), WorkspaceId = Owner, Name = "Atlas", Description = "", MascotUrl = "x" },
        ]);
        // The owner's stored chats only: another workspace holds none of these keys.
        _conversations.Setup(c => c.FindByConversationKeysAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string owner, IReadOnlyCollection<string> keys, CancellationToken _) =>
                (IReadOnlyDictionary<string, (Guid Connection, string Sender)>)keys.Where(k => owner == Owner && _inbox.ContainsKey(k)).ToDictionary(k => k, k => _inbox[k]));
        _sut = new AiOperationsService(_activity, _agents.Object, _orchestrator.Object, _waiting, _conversations.Object, _takeovers.Object,
            new MemoryCache(new MemoryCacheOptions()), new FixedTimeProvider(Now), Mock.Of<IAppLogger<AiOperationsService>>());
    }

    // An open item on the AI service's waiting list: a hand-over unless it says otherwise.
    private WaitingListItem Waiting(
        string id, string? conversation = AdaChat, string kind = "handover", string? agent = Nova, int questions = 1,
        string? approval = null, string? customerName = "Ada", int minutesAgo = 20)
    {
        WaitingListItem item = new()
        {
            Id = id,
            Status = WaitingListItem.OpenStatus,
            Kind = kind,
            AgentId = agent,
            CustomerRef = "2348012345678",
            CustomerName = customerName,
            ConversationKey = conversation,
            Summary = $"Summary of {id}",
            Priority = "P2",
            QuestionCount = questions,
            EscalationId = $"esc_{id}",
            OpenedAt = Now.UtcDateTime.AddMinutes(-minutesAgo - 30),
            LastAskedAt = Now.UtcDateTime.AddMinutes(-minutesAgo),
            Approval = approval is null ? null : new WaitingListApproval($"apr_{id}", "payments.refund", "Refund above the limit", approval),
        };
        _waiting.Items.Add(item);
        return item;
    }

    private AgentActivityRecord Turn(
        string turn, string status, DateTime at, string conversation = AdaChat, string channel = "whatsapp",
        string agent = Nova, string? escalation = null, string? approval = null, string? customerName = "Ada", string owner = Owner)
    {
        AgentActivityRecord record = new()
        {
            OwnerUserId = owner,
            Type = AgentActivityTypes.TurnCompleted,
            EventKey = $"{turn}:{status}",
            TurnId = turn,
            ConversationKey = conversation,
            Status = status,
            Channel = channel,
            AgentId = agent,
            EscalationId = escalation,
            ApprovalId = approval,
            CustomerRef = "2348012345678",
            CustomerName = customerName,
            OccurredAt = at,
            Payload = "{}",
        };
        _activity.Records.Add(record);
        return record;
    }

    private AgentActivityRecord Approval(string id, string turn, DateTime at, DateTime? resolvedAt = null, string owner = Owner, string? conversation = AdaChat)
    {
        AgentActivityRecord record = new()
        {
            OwnerUserId = owner,
            Type = AgentActivityTypes.ApprovalRequested,
            EventKey = id,
            ConversationKey = conversation,
            ApprovalId = id,
            TurnId = turn,
            Tool = "payments.refund",
            Reason = "Refund above the limit",
            CustomerRef = "2348012345678",
            OccurredAt = at,
            Payload = "{}",
            ResolvedAt = resolvedAt,
            Resolution = resolvedAt is null ? null : "approved",
        };
        _activity.Records.Add(record);
        return record;
    }

    private AgentActivityRecord Escalation(
        string id, string turn, DateTime at, string? approval = null, DateTime? resolvedAt = null, string? conversation = null, string owner = Owner)
    {
        AgentActivityRecord record = new()
        {
            OwnerUserId = owner,
            Type = AgentActivityTypes.EscalationOpened,
            EventKey = id,
            ConversationKey = conversation,
            EscalationId = id,
            ApprovalId = approval,
            TurnId = turn,
            Priority = "P2",
            Problem = "No policy for exchanges",
            Channel = "whatsapp",
            CustomerRef = "2348012345678",
            OccurredAt = at,
            Payload = "{}",
            ResolvedAt = resolvedAt,
        };
        _activity.Records.Add(record);
        return record;
    }

    [Fact]
    public async Task TodayAsync_CountsConversationsOutcomesTasksAndTheTeamsReplyTime()
    {
        Turn("t1", "completed", Today.AddHours(9), conversation: "whatsapp:a:b");
        Turn("t2", "completed", Today.AddHours(10), conversation: "whatsapp:a:b");
        Turn("t3", "escalated", Today.AddHours(11), conversation: "whatsapp:c:b", escalation: "esc_1");
        Turn("t4", "failed", Today.AddHours(12), conversation: "whatsapp:d:b");
        Turn("t5", "completed", Today.AddDays(-1).AddHours(12), conversation: "whatsapp:e:b");
        Turn("t6", "completed", Today.AddHours(12), conversation: "whatsapp:f:b", owner: "someone-else");
        Approval("apr_1", "t3", Today.AddHours(11), resolvedAt: Today.AddHours(11).AddMinutes(20));
        Approval("apr_0", "t0", Today.AddDays(-2), resolvedAt: Today.AddDays(-2).AddMinutes(90));

        AgentTodayResponse today = await _sut.TodayAsync(Owner, null, null);

        Assert.Equal(3, today.Conversations);
        Assert.Equal(2, today.AnsweredAlone);
        Assert.Equal(2, today.PassedToTeam);
        Assert.Equal(1, today.TasksCreated);
        Assert.Equal(20, today.TeamReplyMinutes);
    }

    [Fact]
    public async Task TodayAsync_WithNothingYet_IsAllZerosAndNoReplyTime()
    {
        AgentTodayResponse today = await _sut.TodayAsync(Owner, null, null);

        Assert.Equal(new AgentTodayResponse(0, 0, 0, 0, null), today);
    }

    // The orchestrator reports a turn twice when it waited for an approval; the later report says how it ended.
    [Fact]
    public async Task TodayAsync_ATurnThatWaitedForApproval_CountsOnceByItsFinalStatus()
    {
        Turn("t1", "awaiting_approval", Today.AddHours(9), approval: "apr_1");
        Turn("t1", "completed", Today.AddHours(9).AddMinutes(15));

        AgentTodayResponse today = await _sut.TodayAsync(Owner, null, null);

        Assert.Equal(1, today.Conversations);
        Assert.Equal(1, today.AnsweredAlone);
        Assert.Equal(0, today.PassedToTeam);
    }

    [Fact]
    public async Task TodayAsync_FiltersByAgentAndByChannel()
    {
        Turn("t1", "completed", Today.AddHours(9), conversation: "whatsapp:a:b", agent: Nova, channel: "whatsapp");
        Turn("t2", "completed", Today.AddHours(9), conversation: "instagram:a:b", agent: Nova, channel: "instagram");
        Turn("t3", "escalated", Today.AddHours(9), conversation: "whatsapp:c:b", agent: Atlas, channel: "whatsapp");

        AgentTodayResponse nova = await _sut.TodayAsync(Owner, Nova, null);
        AgentTodayResponse novaOnInstagram = await _sut.TodayAsync(Owner, Nova, "Instagram");
        AgentTodayResponse whatsapp = await _sut.TodayAsync(Owner, null, "whatsapp");

        Assert.Equal(2, nova.Conversations);
        Assert.Equal(1, novaOnInstagram.Conversations);
        Assert.Equal(2, whatsapp.Conversations);
        Assert.Equal(1, whatsapp.PassedToTeam);
    }

    [Fact]
    public async Task TodayAsync_SkippedTurns_AreNotActivity()
    {
        Turn("t1", "skipped", Today.AddHours(9));

        Assert.Equal(0, (await _sut.TodayAsync(Owner, null, null)).Conversations);
    }

    [Fact]
    public async Task WeekAsync_ReturnsSevenDaysOldestFirstTodayLast()
    {
        Turn("t1", "completed", Today.AddDays(-6).AddHours(8), conversation: "whatsapp:a:b");
        Turn("t2", "completed", Today.AddDays(-6).AddHours(9), conversation: "whatsapp:a:b");
        Turn("t3", "completed", Today.AddDays(-7).AddHours(9), conversation: "whatsapp:z:b");
        Turn("t4", "escalated", Today.AddDays(-2).AddHours(9), conversation: "whatsapp:c:b");
        Turn("t5", "completed", Today.AddDays(-2).AddHours(10), conversation: "whatsapp:d:b");
        Turn("t6", "completed", Today.AddHours(14), conversation: "whatsapp:e:b");

        IReadOnlyList<int> week = await _sut.WeekAsync(Owner, null, null);

        Assert.Equal([1, 0, 0, 0, 2, 0, 1], week);
    }

    [Fact]
    public async Task RecentAsync_ListsTheLatestTurnsNewestFirstWithHowTheyEnded()
    {
        Turn("t1", "completed", Now.UtcDateTime.AddMinutes(-30), customerName: "Ada");
        Turn("t2", "awaiting_approval", Now.UtcDateTime.AddMinutes(-20), conversation: "whatsapp:c:b", customerName: null, approval: "apr_1");
        Turn("t2", "escalated", Now.UtcDateTime.AddMinutes(-5), conversation: "whatsapp:c:b", customerName: null, escalation: "esc_1");
        Escalation("esc_1", "t2", Now.UtcDateTime.AddMinutes(-5));

        IReadOnlyList<RecentConversationResponse> recent = await _sut.RecentAsync(Owner, null, null, 20);

        Assert.Equal(2, recent.Count);
        Assert.Equal("TaskCreated", recent[0].Outcome);
        Assert.Equal("No policy for exchanges", recent[0].Task);
        Assert.Equal("2348012345678", recent[0].Customer);
        Assert.Equal(5, recent[0].MinutesAgo);
        Assert.Equal("t2", recent[0].TurnId);
        Assert.Equal("Answered", recent[1].Outcome);
        Assert.Equal("Ada", recent[1].Customer);
        Assert.Equal(30, recent[1].MinutesAgo);
        Assert.Equal("whatsapp:2348012345678:2349011112222", recent[1].ConversationKey);
    }

    [Fact]
    public async Task RecentAsync_HonoursTheLimit()
    {
        for (int i = 0; i < 5; i++) Turn($"t{i}", "completed", Now.UtcDateTime.AddMinutes(-i), conversation: $"whatsapp:{i}:b");

        Assert.Equal(2, (await _sut.RecentAsync(Owner, null, null, 2)).Count);
    }

    [Fact]
    public async Task WaitingAsync_ListsTheAiServicesOpenItemsWithTheAgentCustomerAndInboxConversation()
    {
        Waiting("wait_ada", agent: Nova, minutesAgo: 40);
        Waiting("wait_bola", BolaChat, kind: "approval", agent: Atlas, approval: "pending", customerName: null, minutesAgo: 10);
        Waiting("wait_kunle", KunleChat, agent: "an-agent-parrot-does-not-know", minutesAgo: 5);

        IReadOnlyList<WaitingItemResponse> waiting = await _sut.WaitingAsync(Owner, Token, null);

        Assert.Equal(["esc_wait_ada", "esc_wait_bola", "esc_wait_kunle"], waiting.Select(w => w.EscalationId));
        WaitingItemResponse ada = waiting[0];
        Assert.Equal("Nova", ada.AgentName);
        Assert.Equal(Nova, ada.AgentId);
        Assert.Equal("Ada", ada.Customer);
        Assert.Equal("Summary of wait_ada", ada.Question);
        Assert.Null(ada.ApprovalId);
        Assert.Equal("P2", ada.Priority);
        Assert.Equal(Now.UtcDateTime.AddMinutes(-70), ada.OccurredAt);
        Assert.Equal($"{WhatsApp}:2348012345678", ada.ConversationId);
        WaitingItemResponse bola = waiting[1];
        Assert.Equal("Atlas", bola.AgentName);
        Assert.Equal("2348012345678", bola.Customer);
        Assert.Equal("apr_wait_bola", bola.ApprovalId);
        Assert.Equal($"{WhatsApp}:2348022222222", bola.ConversationId);
        Assert.Equal("Your agent", waiting[2].AgentName);
        // Read as the signed-in person: the AI service takes the workspace from their token.
        Assert.Equal([Token], _waiting.ListReads);
    }

    // Nothing in the inbox to answer it in (a chat Parrot never stored, or no key at all), so it can't be worked from here.
    [Fact]
    public async Task WaitingAsync_WhenTheConversationIsNotInTheInbox_LeavesTheItemOut()
    {
        Waiting("wait_test", "whatsapp:2340000000000:2349011112222");
        Waiting("wait_nokey", conversation: null);
        Waiting("wait_ada");

        WaitingItemResponse item = Assert.Single(await _sut.WaitingAsync(Owner, Token, null));

        Assert.Equal("esc_wait_ada", item.EscalationId);
    }

    [Fact]
    public async Task WaitingAsync_ForATeammateLimitedToSomeChannels_LeavesOutItemsOnTheOthers()
    {
        Waiting("wait_ada");
        Waiting("wait_bayo", BayoChat);

        IReadOnlyList<WaitingItemResponse> everything = await _sut.WaitingAsync(Owner, Token, null);
        IReadOnlyList<WaitingItemResponse> whatsAppOnly = await _sut.WaitingAsync(Owner, Token, new HashSet<Guid> { WhatsApp });

        Assert.Equal(2, everything.Count);
        Assert.Equal("esc_wait_ada", Assert.Single(whatsAppOnly).EscalationId);
    }

    // The inbox re-reads on a timer in every open tab; the AI service allows 120 reads a minute per workspace.
    [Fact]
    public async Task WaitingAsync_ReadsTheAiServiceOncePerWorkspaceWhileTheListIsFresh()
    {
        Waiting("wait_ada");

        await _sut.WaitingAsync(Owner, Token, null);
        await _sut.WaitingAsync(Owner, "session-tunde", new HashSet<Guid> { WhatsApp });
        await _sut.OpenByConversationAsync(Owner, Token, null);
        await _sut.OpenQuestionsAsync(Owner, Token, null, WhatsApp, "2348012345678");

        Assert.Single(_waiting.ListReads);

        // Another workspace never shares this one's list, and its own read can't link this workspace's chats.
        _agents.Setup(a => a.GetByWorkspaceAsync("someone-else")).ReturnsAsync([]);
        Assert.Empty(await _sut.WaitingAsync("someone-else", "session-theirs", null));
        Assert.Equal([Token, "session-theirs"], _waiting.ListReads);
    }

    // The inbox and the Agents page still load; nothing shows as waiting, and the service isn't asked again straight away.
    [Fact]
    public async Task WaitingAsync_WhenTheAiServiceDoesNotAnswer_IsEmptyAndDoesNotAskAgainAtOnce()
    {
        Waiting("wait_ada");
        _waiting.Failure = new ServiceUnavailableException("The AI service could not be reached.", new TimeoutException());

        Assert.Empty(await _sut.WaitingAsync(Owner, Token, null));
        Assert.Empty(await _sut.OpenByConversationAsync(Owner, Token, null));
        Assert.Empty(await _sut.OpenQuestionsAsync(Owner, Token, null, WhatsApp, "2348012345678"));

        Assert.Single(_waiting.ListReads);
    }

    [Fact]
    public async Task OpenByConversationAsync_SaysWhatEachConversationWaitsOnAndWhichAgentAsked()
    {
        Waiting("wait_ada", agent: Nova);
        Waiting("wait_bola", BolaChat, kind: "approval", agent: Atlas, approval: "pending");
        // An approval the customer has asked more since: both a decision and an answer.
        Waiting("wait_kunle", KunleChat, kind: "approval", approval: "pending", questions: 2);
        // An approval that expired can't be decided any more; the customer still waits for a reply.
        Waiting("wait_tayo", TayoChat, kind: "handover", approval: "expired", agent: null);

        Dictionary<string, ConversationQuestions> open = (await _sut.OpenByConversationAsync(Owner, Token, null)).ToDictionary(q => q.Sender);

        Assert.Equal(4, open.Count);
        Assert.True(open["2348012345678"].NeedsAnswer);
        Assert.Null(open["2348012345678"].Approval);
        Assert.Equal(new ConversationAgentRef(Nova, "Nova"), open["2348012345678"].Agent);
        Assert.False(open["2348022222222"].NeedsAnswer);
        Assert.Equal(new ConversationApproval("apr_wait_bola", "payments.refund", "Refund above the limit"), open["2348022222222"].Approval);
        Assert.Equal(new ConversationAgentRef(Atlas, "Atlas"), open["2348022222222"].Agent);
        Assert.True(open["2348033333333"].NeedsAnswer);
        Assert.Equal("apr_wait_kunle", open["2348033333333"].Approval?.ApprovalId);
        Assert.True(open["2348044444444"].NeedsAnswer);
        Assert.Null(open["2348044444444"].Approval);
        Assert.Null(open["2348044444444"].Agent);
        Assert.All(open.Values, q => Assert.Equal(WhatsApp, q.ConnectionId));
    }

    [Fact]
    public async Task OpenByConversationAsync_WithNothingWaiting_IsEmptyAndReadsNoConversations()
    {
        Assert.Empty(await _sut.OpenByConversationAsync(Owner, Token, null));
        _conversations.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task OpenQuestionsAsync_ShowsTheItemAsAnAgentQuestionAtItsNewestHandOver()
    {
        Waiting("wait_ada", questions: 3, minutesAgo: 12);
        Waiting("wait_bola", BolaChat, kind: "approval", approval: "pending");

        ConversationMessage question = Assert.Single(await _sut.OpenQuestionsAsync(Owner, Token, null, WhatsApp, "2348012345678"));

        Assert.Equal("wait_ada", question.Id);
        Assert.Equal("ai_question", question.Kind);
        Assert.Equal("Summary of wait_ada", question.Text);
        Assert.Equal(Now.UtcDateTime.AddMinutes(-12), question.ReceivedAt);
        // An approval and nothing more is decided on its own card, not answered.
        Assert.Empty(await _sut.OpenQuestionsAsync(Owner, Token, null, WhatsApp, "2348022222222"));
    }

    [Fact]
    public async Task OpenQuestionsAsync_ForATeammateWhoCannotOpenTheChannel_IsEmpty()
    {
        Waiting("wait_ada");

        Assert.Empty(await _sut.OpenQuestionsAsync(Owner, Token, new HashSet<Guid> { Instagram }, WhatsApp, "2348012345678"));
    }

    [Fact]
    public async Task CloseQuestionAsync_MarksItHandledAsThePersonSaysSoInTheChatAndTheNextReadIsFresh()
    {
        Waiting("wait_ada");
        Assert.Single(await _sut.WaitingAsync(Owner, Token, null));

        await _sut.CloseQuestionAsync(Owner, Token, null, WhatsApp, "2348012345678", "wait_ada", "user-ngozi");

        Assert.Equal(["wait_ada"], _waiting.Resolved);
        Assert.Equal("user-ngozi", _waiting.Items.Single().ResolvedBy);
        _conversations.Verify(c => c.WriteSystemEventAsync(Owner, WhatsApp, "2348012345678",
            "A team member marked the agent's question as done.", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Empty(await _sut.WaitingAsync(Owner, Token, null));
        Assert.Equal(2, _waiting.ListReads.Count);
    }

    // One in another chat, one already closed, an approval with nothing to answer, and one the AI service doesn't have
    // in this workspace (another workspace's, or one that never existed) all answer the same, and nothing is closed.
    [Theory]
    [InlineData("wait_bola")]
    [InlineData("wait_closed")]
    [InlineData("wait_approval")]
    [InlineData("wait_never")]
    public async Task CloseQuestionAsync_ForAQuestionNotOpenInThisChat_ThrowsTheSameNotFoundAndClosesNothing(string questionId)
    {
        Waiting("wait_bola", BolaChat);
        Waiting("wait_closed");
        _waiting.Close("wait_closed", "team_replied", null);
        Waiting("wait_approval", kind: "approval", approval: "pending");

        NotFoundException error = await Assert.ThrowsAsync<NotFoundException>(() =>
            _sut.CloseQuestionAsync(Owner, Token, null, WhatsApp, "2348012345678", questionId, "user-ngozi"));

        Assert.Equal("Question not found.", error.Message);
        Assert.Empty(_waiting.Resolved);
        _conversations.Verify(c => c.WriteSystemEventAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CloseQuestionAsync_OnAChannelTheTeammateCannotOpen_ThrowsNotFoundAndClosesNothing()
    {
        Waiting("wait_ada");

        NotFoundException error = await Assert.ThrowsAsync<NotFoundException>(() =>
            _sut.CloseQuestionAsync(Owner, Token, new HashSet<Guid> { Instagram }, WhatsApp, "2348012345678", "wait_ada", "user-ngozi"));

        Assert.Equal("Question not found.", error.Message);
        Assert.Empty(_waiting.Resolved);
    }

    // A teammate in another tab, or a reply to the customer, closed it between the read and the resolve: the first close
    // wins, and only the person whose close it was says so in the chat.
    [Theory]
    [InlineData("handled", "user-tunde")]
    [InlineData("team_replied", null)]
    public async Task CloseQuestionAsync_WhenSomethingElseClosedItFirst_WritesNoChatLine(string resolution, string? by)
    {
        Waiting("wait_ada");
        _waiting.BeforeResolve = item => _waiting.Close(item.Id, resolution, by);

        await _sut.CloseQuestionAsync(Owner, Token, null, WhatsApp, "2348012345678", "wait_ada", "user-ngozi");

        _conversations.Verify(c => c.WriteSystemEventAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CloseQuestionAsync_WhenTheChatLineCannotBeWritten_StillClosesTheQuestion()
    {
        Waiting("wait_ada");
        _conversations.Setup(c => c.WriteSystemEventAsync(Owner, WhatsApp, "2348012345678", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("mongo"));

        await _sut.CloseQuestionAsync(Owner, Token, null, WhatsApp, "2348012345678", "wait_ada", "user-ngozi");

        Assert.Equal(["wait_ada"], _waiting.Resolved);
    }

    // Nothing was closed, so the person is told to try again rather than that it is done.
    [Fact]
    public async Task CloseQuestionAsync_WhenTheAiServiceDoesNotAnswer_FailsAndWritesNothing()
    {
        Waiting("wait_ada");
        _waiting.Failure = new ServiceUnavailableException("The AI service could not be reached.", new TimeoutException());

        await Assert.ThrowsAsync<ServiceUnavailableException>(() =>
            _sut.CloseQuestionAsync(Owner, Token, null, WhatsApp, "2348012345678", "wait_ada", "user-ngozi"));

        _conversations.Verify(c => c.WriteSystemEventAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private Task<QuestionAnswerResponse> Answer(
        string? answer = "Yes, students get 10% off their first purchase.", bool saveForNextTime = true, string questionId = "wait_ada",
        IReadOnlySet<Guid>? reach = null) =>
        _sut.AnswerQuestionAsync(Owner, Token, reach, WhatsApp, "2348012345678", questionId, new QuestionAnswerRequest(answer, saveForNextTime));

    private void NoChatLineWritten() =>
        _conversations.Verify(c => c.WriteSystemEventAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

    [Fact]
    public async Task AnswerQuestionAsync_HandsTheAnswerToTheAgentWhichRepliesToTheCustomerAndTheNextReadIsFresh()
    {
        Waiting("wait_ada");
        Assert.Single(await _sut.WaitingAsync(Owner, Token, null));

        QuestionAnswerResponse response = await Answer("  Yes, students get 10% off their first purchase.  ");

        Assert.True(response.AgentReplies);
        (string itemId, WaitingListAnswer sent) = Assert.Single(_waiting.Answers);
        Assert.Equal("wait_ada", itemId);
        Assert.Equal(new WaitingListAnswer("Yes, students get 10% off their first purchase.", ReplyToCustomer: true, SaveForNextTime: true), sent);
        Assert.Equal("user-ngozi", _waiting.Items.Single().ResolvedBy);
        _conversations.Verify(c => c.WriteSystemEventAsync(Owner, WhatsApp, "2348012345678",
            "A team member answered the agent's question.", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Empty(await _sut.WaitingAsync(Owner, Token, null));
        Assert.Equal(2, _waiting.ListReads.Count);
    }

    [Fact]
    public async Task AnswerQuestionAsync_WithSaveForNextTimeOff_SaysSoToTheAiService()
    {
        Waiting("wait_ada");

        await Answer(saveForNextTime: false);

        Assert.False(Assert.Single(_waiting.Answers).Answer.SaveForNextTime);
    }

    // Someone is replying in the chat themselves: the agent takes the answer, but the customer hears it from them.
    [Fact]
    public async Task AnswerQuestionAsync_WhenAPersonHasTakenOver_TheAgentSendsTheCustomerNothing()
    {
        Waiting("wait_ada");
        _takeovers.Setup(t => t.IsActiveAsync(Owner, WhatsApp, "2348012345678", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        QuestionAnswerResponse response = await Answer();

        Assert.False(response.AgentReplies);
        Assert.False(Assert.Single(_waiting.Answers).Answer.ReplyToCustomer);
    }

    // One in another chat, one already closed, an approval with nothing to answer, and one the AI service doesn't have
    // in this workspace (another workspace's, or one that never existed) all answer the same, and nothing is sent.
    [Theory]
    [InlineData("wait_bola")]
    [InlineData("wait_closed")]
    [InlineData("wait_approval")]
    [InlineData("wait_never")]
    public async Task AnswerQuestionAsync_ForAQuestionNotOpenInThisChat_ThrowsTheSameNotFoundAndSendsNothing(string questionId)
    {
        Waiting("wait_bola", BolaChat);
        Waiting("wait_closed");
        _waiting.Close("wait_closed", "team_replied", null);
        Waiting("wait_approval", kind: "approval", approval: "pending");

        NotFoundException error = await Assert.ThrowsAsync<NotFoundException>(() => Answer(questionId: questionId));

        Assert.Equal("Question not found.", error.Message);
        Assert.Empty(_waiting.Answers);
        NoChatLineWritten();
    }

    [Fact]
    public async Task AnswerQuestionAsync_OnAChannelTheTeammateCannotOpen_ThrowsNotFoundAndSendsNothing()
    {
        Waiting("wait_ada");

        NotFoundException error = await Assert.ThrowsAsync<NotFoundException>(() => Answer(reach: new HashSet<Guid> { Instagram }));

        Assert.Equal("Question not found.", error.Message);
        Assert.Empty(_waiting.Answers);
    }

    // Read as open, then gone by the time the answer arrives: the same not found, and no line in the chat.
    [Fact]
    public async Task AnswerQuestionAsync_WhenTheAiServiceNoLongerHasIt_ThrowsTheSameNotFound()
    {
        Waiting("wait_ada");
        _waiting.BeforeAnswer = item => _waiting.Items.Remove(item);

        NotFoundException error = await Assert.ThrowsAsync<NotFoundException>(() => Answer());

        Assert.Equal("Question not found.", error.Message);
        NoChatLineWritten();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnswerQuestionAsync_WithNoAnswer_ThrowsValidationAndSendsNothing(string? answer)
    {
        Waiting("wait_ada");

        ValidationException error = await Assert.ThrowsAsync<ValidationException>(() => Answer(answer));

        Assert.Equal("Write your answer for the agent.", error.Message);
        Assert.Empty(_waiting.Answers);
    }

    [Fact]
    public async Task AnswerQuestionAsync_WithAnAnswerOverTheLimit_ThrowsValidationAndSendsNothing()
    {
        Waiting("wait_ada");

        ValidationException error = await Assert.ThrowsAsync<ValidationException>(() => Answer(new string('a', AiOperationsService.MaxAnswer + 1)));

        Assert.Equal("Keep your answer to 1,000 characters.", error.Message);
        Assert.Empty(_waiting.Answers);
    }

    [Fact]
    public async Task AnswerQuestionAsync_WhenTheChatLineCannotBeWritten_StillAnswers()
    {
        Waiting("wait_ada");
        _conversations.Setup(c => c.WriteSystemEventAsync(Owner, WhatsApp, "2348012345678", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("mongo"));

        QuestionAnswerResponse response = await Answer();

        Assert.True(response.AgentReplies);
        Assert.Single(_waiting.Answers);
    }

    // Nothing reached the agent, so the person is told to try again rather than that it is answered.
    [Fact]
    public async Task AnswerQuestionAsync_WhenTheAiServiceDoesNotAnswer_FailsAndWritesNothing()
    {
        Waiting("wait_ada");
        _waiting.Failure = new ServiceUnavailableException("The AI service could not be reached.", new TimeoutException());

        await Assert.ThrowsAsync<ServiceUnavailableException>(() => Answer());

        NoChatLineWritten();
    }

    // Bola's refund, waiting at the AI service, and Parrot's own record of the request (it feeds the day's figures).
    private void BolaApproval()
    {
        Approval("apr_wait_bola", "t1", Now.UtcDateTime.AddMinutes(-10), conversation: BolaChat);
        Waiting("wait_bola", BolaChat, kind: "approval", approval: "pending");
    }

    private void OrchestratorAnswers(string? status, string? reply = null) =>
        _orchestrator.Setup(o => o.DecideApprovalAsync(Owner, "apr_wait_bola", It.IsAny<ApprovalDecision>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApprovalDecisionResult("t1", status, reply));

    private Task<ApprovalDecisionResponse> Decide(string decision = "approved", string? instruction = null, IReadOnlySet<Guid>? reach = null, string approvalId = "apr_wait_bola") =>
        _sut.DecideApprovalAsync(Owner, Token, reach, "user-ngozi", approvalId, new ApprovalDecisionRequest(decision, instruction));

    [Fact]
    public async Task DecideApprovalAsync_SendsTheDecisionAsThePersonAndRecordsIt()
    {
        BolaApproval();
        OrchestratorAnswers("completed", "Your refund is on its way.");

        ApprovalDecisionResponse response = await Decide("Approved", "  Refund in full ");

        Assert.Equal("approved", response.Decision);
        Assert.Equal("completed", response.Status);
        Assert.Equal("Your refund is on its way.", response.Reply);
        // The AI service records who decided as the item's resolved_by: the person, not the workspace.
        _orchestrator.Verify(o => o.DecideApprovalAsync(Owner, "apr_wait_bola",
            It.Is<ApprovalDecision>(d => d.Decision == "approved" && d.DecidedBy == "user-ngozi" && d.Instruction == "Refund in full"),
            It.IsAny<CancellationToken>()), Times.Once);
        AgentActivityRecord record = Assert.Single(_activity.Records);
        Assert.Equal("approved", record.Resolution);
        Assert.Equal("user-ngozi", record.ResolvedBy);
        Assert.Equal(Now.UtcDateTime, record.ResolvedAt);
    }

    // Deciding closes the approval's waiting item at the AI service; the list isn't served from the copy read before.
    [Fact]
    public async Task DecideApprovalAsync_AfterTheDecision_ReadsTheWaitingListAfresh()
    {
        BolaApproval();
        Assert.Single(await _sut.WaitingAsync(Owner, Token, null));
        _orchestrator.Setup(o => o.DecideApprovalAsync(Owner, "apr_wait_bola", It.IsAny<ApprovalDecision>(), It.IsAny<CancellationToken>()))
            .Callback(() => _waiting.Close("wait_bola", "approved", "user-ngozi"))
            .ReturnsAsync(new ApprovalDecisionResult("t1", "completed", null));

        await Decide();

        Assert.Empty(await _sut.WaitingAsync(Owner, Token, null));
    }

    // Never asked here (or another workspace's), one whose item has closed, one that expired (the AI service then calls
    // the item a hand-over) and one in a chat Parrot never stored all answer the same, and nothing is sent.
    [Theory]
    [InlineData("apr_never")]
    [InlineData("apr_wait_closed")]
    [InlineData("apr_wait_expired")]
    [InlineData("apr_wait_unstored")]
    public async Task DecideApprovalAsync_ForAnApprovalNotWaitingHere_ThrowsTheSameNotFoundAndSendsNothing(string approvalId)
    {
        Waiting("wait_closed", BolaChat, kind: "approval", approval: "pending");
        _waiting.Close("wait_closed", "handled", "user-tunde");
        Waiting("wait_expired", KunleChat, kind: "handover", approval: "expired");
        Waiting("wait_unstored", "whatsapp:2340000000000:2349011112222", kind: "approval", approval: "pending");

        NotFoundException error = await Assert.ThrowsAsync<NotFoundException>(() => Decide(approvalId: approvalId));

        Assert.Equal("No approval with that id is waiting for a decision.", error.Message);
        _orchestrator.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DecideApprovalAsync_OnAChannelTheTeammateCannotOpen_ThrowsNotFoundAndSendsNothing()
    {
        BolaApproval();

        NotFoundException error = await Assert.ThrowsAsync<NotFoundException>(() => Decide(reach: new HashSet<Guid> { Instagram }));

        Assert.Equal("No approval with that id is waiting for a decision.", error.Message);
        _orchestrator.VerifyNoOtherCalls();
    }

    // The chat can show an approval from the agent's live event a few seconds before the shared copy is read again.
    [Fact]
    public async Task DecideApprovalAsync_ForOneAskedSinceTheListWasRead_ReadsAfreshAndDecidesIt()
    {
        Assert.Empty(await _sut.WaitingAsync(Owner, Token, null));
        BolaApproval();
        OrchestratorAnswers("completed");

        await Decide("denied");

        Assert.Equal(2, _waiting.ListReads.Count);
        _orchestrator.Verify(o => o.DecideApprovalAsync(Owner, "apr_wait_bola", It.Is<ApprovalDecision>(d => d.Decision == "denied"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // An empty list may only mean the AI service didn't answer: the person is told to try again, not that it isn't waiting.
    [Fact]
    public async Task DecideApprovalAsync_WhenTheWaitingListCannotBeRead_FailsAndSendsNothing()
    {
        BolaApproval();
        _waiting.Failure = new ServiceUnavailableException("The AI service could not be reached.", new TimeoutException());

        await Assert.ThrowsAsync<ServiceUnavailableException>(() => Decide());

        _orchestrator.VerifyNoOtherCalls();
    }

    // Someone else's decision came first and stands.
    [Fact]
    public async Task DecideApprovalAsync_WhenSomeoneDecidedItFirst_SaysSoAndRecordsNothingOfThisPersons()
    {
        BolaApproval();
        OrchestratorAnswers(ApprovalDecisionStatus.AlreadyDecided);

        ApprovalDecisionResponse response = await Decide();

        Assert.Equal("already_decided", response.Status);
        Assert.Null(Assert.Single(_activity.Records).ResolvedAt);
    }

    // Recorded at the AI service either way: the agent acts on it once it has finished replying, or nothing was waiting on it.
    [Theory]
    [InlineData(ApprovalDecisionStatus.ConversationBusy)]
    [InlineData(ApprovalDecisionStatus.NoTurn)]
    public async Task DecideApprovalAsync_WhenTheDecisionIsRecordedButNotActedOnNow_SaysSoAndRecordsIt(string status)
    {
        BolaApproval();
        OrchestratorAnswers(status);

        ApprovalDecisionResponse response = await Decide();

        Assert.Equal(status, response.Status);
        Assert.Equal("approved", Assert.Single(_activity.Records).Resolution);
    }

    [Fact]
    public async Task DecideApprovalAsync_WhenTheAiServiceHasNoSuchApproval_ThrowsTheSameNotFound()
    {
        BolaApproval();
        _orchestrator.Setup(o => o.DecideApprovalAsync(Owner, "apr_wait_bola", It.IsAny<ApprovalDecision>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotFoundException("The AI service has no record of that."));

        NotFoundException error = await Assert.ThrowsAsync<NotFoundException>(() => Decide());

        Assert.Equal("No approval with that id is waiting for a decision.", error.Message);
        Assert.Null(Assert.Single(_activity.Records).ResolvedAt);
    }

    // The AI service has the decision, which is what the agent acts on; Parrot's copy only feeds the day's figures.
    [Fact]
    public async Task DecideApprovalAsync_WhenParrotsOwnRecordCannotBeWritten_StillSucceeds()
    {
        BolaApproval();
        OrchestratorAnswers("completed", "Done.");
        _activity.Failure = new TimeoutException("mongo");

        ApprovalDecisionResponse response = await Decide();

        Assert.Equal("Done.", response.Reply);
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("")]
    public async Task DecideApprovalAsync_WithADecisionThatIsNeither_ThrowsValidation(string decision)
    {
        BolaApproval();

        await Assert.ThrowsAsync<ValidationException>(() => Decide(decision));

        _orchestrator.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DecideApprovalAsync_WithAnInstructionOverTheAiServicesLimit_ThrowsValidationAndSendsNothing()
    {
        BolaApproval();

        ValidationException error = await Assert.ThrowsAsync<ValidationException>(() => Decide(instruction: new string('a', 1001)));

        Assert.Equal("Keep the instruction for the agent to 1,000 characters.", error.Message);
        _orchestrator.VerifyNoOtherCalls();
    }

    // The orchestrator did not take the decision, so nothing is marked decided here either.
    [Fact]
    public async Task DecideApprovalAsync_WhenTheOrchestratorIsDown_LeavesTheApprovalWaiting()
    {
        BolaApproval();
        _orchestrator.Setup(o => o.DecideApprovalAsync(Owner, "apr_wait_bola", It.IsAny<ApprovalDecision>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ServiceUnavailableException("down", new TimeoutException()));

        await Assert.ThrowsAsync<ServiceUnavailableException>(() => Decide());

        Assert.Null(Assert.Single(_activity.Records).ResolvedAt);
    }
}
