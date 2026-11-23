using Moq;
using Parrot.Application.Agents;
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

    private readonly FakeAgentActivityStore _activity = new();
    private readonly Mock<IAgentRepository> _agents = new();
    private readonly Mock<IAgentOrchestratorClient> _orchestrator = new();
    private readonly Mock<IConversationStore> _conversations = new();
    private readonly Dictionary<string, (Guid Connection, string Sender)> _inbox = new() { [AdaChat] = (WhatsApp, "2348012345678") };
    // When the team last replied from the inbox, per chat.
    private readonly Dictionary<(Guid Connection, string Sender), DateTime> _replies = [];
    private readonly AiOperationsService _sut;

    public AiOperationsServiceTests()
    {
        _agents.Setup(a => a.GetByWorkspaceAsync(Owner)).ReturnsAsync(
        [
            new Agent { Id = Guid.Parse(Nova), WorkspaceId = Owner, Name = "Nova", Description = "", MascotUrl = "x" },
            new Agent { Id = Guid.Parse(Atlas), WorkspaceId = Owner, Name = "Atlas", Description = "", MascotUrl = "x" },
        ]);
        // The owner's stored chats only: the store is asked for this owner, and answers for the keys it holds.
        _conversations.Setup(c => c.FindByConversationKeysAsync(Owner, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, IReadOnlyCollection<string> keys, CancellationToken _) =>
                (IReadOnlyDictionary<string, (Guid Connection, string Sender)>)keys.Where(_inbox.ContainsKey).ToDictionary(k => k, k => _inbox[k]));
        _conversations.Setup(c => c.LastTeamRepliesAsync(Owner, It.IsAny<IReadOnlyCollection<(Guid, string)>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, IReadOnlyCollection<(Guid Connection, string Sender)> wanted, CancellationToken _) =>
                (IReadOnlyDictionary<(Guid Connection, string Sender), DateTime>)wanted.Where(_replies.ContainsKey).ToDictionary(k => k, k => _replies[k]));
        _sut = new AiOperationsService(_activity, _agents.Object, _orchestrator.Object, _conversations.Object, new FixedTimeProvider(Now),
            Mock.Of<IAppLogger<AiOperationsService>>());
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
    public async Task WaitingAsync_ListsPendingApprovalsAndEscalationsNamedForTheAgentThatAsked()
    {
        Turn("t1", "awaiting_approval", Now.UtcDateTime.AddMinutes(-10), agent: Atlas, customerName: "Ada", approval: "apr_1");
        Approval("apr_1", "t1", Now.UtcDateTime.AddMinutes(-10));
        Approval("apr_old", "t0", Now.UtcDateTime.AddHours(-3), resolvedAt: Now.UtcDateTime.AddHours(-2));
        Turn("t2", "escalated", Now.UtcDateTime.AddMinutes(-40), agent: Nova, customerName: null, escalation: "esc_1");
        Escalation("esc_1", "t2", Now.UtcDateTime.AddMinutes(-40));
        // Opened because apr_1 timed out: the same question as the approval, shown once.
        Escalation("esc_2", "t1", Now.UtcDateTime.AddMinutes(-1), approval: "apr_1");
        Approval("apr_theirs", "t9", Now.UtcDateTime, owner: "someone-else");

        IReadOnlyList<WaitingItemResponse> waiting = await _sut.WaitingAsync(Owner, null);

        Assert.Equal(2, waiting.Count);
        Assert.Equal("Nova", waiting[0].AgentName);
        Assert.Equal("No policy for exchanges", waiting[0].Question);
        Assert.Equal("esc_1", waiting[0].EscalationId);
        Assert.Null(waiting[0].ApprovalId);
        Assert.Equal("P2", waiting[0].Priority);
        Assert.Equal("Atlas", waiting[1].AgentName);
        Assert.Equal("Ada", waiting[1].Customer);
        Assert.Equal("Refund above the limit", waiting[1].Question);
        Assert.Equal("apr_1", waiting[1].ApprovalId);
        Assert.Equal(Nova, waiting[0].AgentId);
        Assert.Equal(Atlas, waiting[1].AgentId);
        Assert.All(waiting, w => Assert.Equal($"{WhatsApp}:2348012345678", w.ConversationId));
    }

    // The escalation names no conversation itself here, so the key comes from the turn that asked it.
    [Fact]
    public async Task WaitingAsync_WhenTheEscalationNamesNoConversation_UsesTheConversationOfTheTurnThatAsked()
    {
        Turn("t1", "escalated", Now.UtcDateTime.AddMinutes(-5), escalation: "esc_1");
        Escalation("esc_1", "t1", Now.UtcDateTime.AddMinutes(-5));

        WaitingItemResponse item = Assert.Single(await _sut.WaitingAsync(Owner, null));

        Assert.Equal($"{WhatsApp}:2348012345678", item.ConversationId);
    }

    // Nothing in the inbox to answer it in, so it would sit in the count for ever.
    [Fact]
    public async Task WaitingAsync_WhenTheConversationIsNotInTheInbox_LeavesTheQuestionOut()
    {
        Escalation("esc_test", "t8", Now.UtcDateTime.AddMinutes(-3), conversation: "whatsapp:2340000000000:2349011112222");
        Escalation("esc_nokey", "t9", Now.UtcDateTime.AddMinutes(-2));
        Escalation("esc_ada", "t7", Now.UtcDateTime.AddMinutes(-1), conversation: AdaChat);

        WaitingItemResponse item = Assert.Single(await _sut.WaitingAsync(Owner, null));

        Assert.Equal("esc_ada", item.EscalationId);
    }

    [Fact]
    public async Task WaitingAsync_ForATeammateLimitedToSomeChannels_LeavesOutQuestionsAskedOnTheOthers()
    {
        const string bayoChat = "instagram:17841400000000001:9876543210";
        _inbox[bayoChat] = (Instagram, "9876543210");
        Escalation("esc_ada", "t1", Now.UtcDateTime.AddMinutes(-2), conversation: AdaChat);
        Escalation("esc_bayo", "t2", Now.UtcDateTime.AddMinutes(-1), conversation: bayoChat);

        IReadOnlyList<WaitingItemResponse> everything = await _sut.WaitingAsync(Owner, null);
        IReadOnlyList<WaitingItemResponse> whatsAppOnly = await _sut.WaitingAsync(Owner, new HashSet<Guid> { WhatsApp });

        Assert.Equal(2, everything.Count);
        Assert.Equal("esc_ada", Assert.Single(whatsAppOnly).EscalationId);
    }

    // Another workspace's question in a chat with the same key never reaches this owner, and their chats are never read.
    [Fact]
    public async Task WaitingAsync_NeverListsAnotherWorkspacesQuestions()
    {
        Escalation("esc_theirs", "t9", Now.UtcDateTime.AddMinutes(-1), conversation: AdaChat, owner: "someone-else");

        Assert.Empty(await _sut.WaitingAsync(Owner, null));
        _conversations.Verify(c => c.FindByConversationKeysAsync("someone-else", It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OpenByConversationAsync_GivesEachConversationItsQuestionApprovalAndTheAgentThatAskedLast()
    {
        const string bayoChat = "whatsapp:2348099999999:2349011112222";
        _inbox[bayoChat] = (WhatsApp, "2348099999999");
        // Ada: Nova escalated, then Atlas asked for an approval.
        Turn("t1", "escalated", Now.UtcDateTime.AddMinutes(-30), agent: Nova, escalation: "esc_1");
        Escalation("esc_1", "t1", Now.UtcDateTime.AddMinutes(-30));
        Turn("t2", "awaiting_approval", Now.UtcDateTime.AddMinutes(-10), agent: Atlas, approval: "apr_1");
        Approval("apr_1", "t2", Now.UtcDateTime.AddMinutes(-10));
        // Bayo: an approval, and the escalation opened when it timed out (the same question).
        Turn("t3", "awaiting_approval", Now.UtcDateTime.AddMinutes(-20), conversation: bayoChat, agent: Nova, approval: "apr_2");
        Approval("apr_2", "t3", Now.UtcDateTime.AddMinutes(-20), conversation: bayoChat);
        Escalation("esc_2", "t3", Now.UtcDateTime.AddMinutes(-5), approval: "apr_2", conversation: bayoChat);

        IReadOnlyList<ConversationQuestions> open = await _sut.OpenByConversationAsync(Owner, null);

        Assert.Equal(2, open.Count);
        ConversationQuestions ada = Assert.Single(open, q => q.Sender == "2348012345678");
        Assert.Equal(WhatsApp, ada.ConnectionId);
        Assert.True(ada.NeedsAnswer);
        Assert.Equal("apr_1", ada.Approval?.ApprovalId);
        Assert.Equal("payments.refund", ada.Approval?.Tool);
        Assert.Equal(new ConversationAgentRef(Atlas, "Atlas"), ada.Agent);
        ConversationQuestions bayo = Assert.Single(open, q => q.Sender == "2348099999999");
        Assert.False(bayo.NeedsAnswer);
        Assert.Equal("apr_2", bayo.Approval?.ApprovalId);
        Assert.Equal(new ConversationAgentRef(Nova, "Nova"), bayo.Agent);
    }

    // The team answered the customer from the inbox after the agent asked: nothing is waiting any more.
    [Fact]
    public async Task WaitingAsync_AfterTheTeamRepliedInThatChat_NoLongerListsTheQuestion()
    {
        Escalation("esc_1", "t1", Now.UtcDateTime.AddMinutes(-30), conversation: AdaChat);
        _replies[(WhatsApp, "2348012345678")] = Now.UtcDateTime.AddMinutes(-10);

        Assert.Empty(await _sut.WaitingAsync(Owner, null));
        Assert.Empty(await _sut.OpenByConversationAsync(Owner, null));
    }

    [Fact]
    public async Task WaitingAsync_WhenTheLastReplyCameBeforeTheQuestion_StillListsIt()
    {
        Escalation("esc_1", "t1", Now.UtcDateTime.AddMinutes(-30), conversation: AdaChat);
        _replies[(WhatsApp, "2348012345678")] = Now.UtcDateTime.AddMinutes(-60);

        Assert.Equal("esc_1", Assert.Single(await _sut.WaitingAsync(Owner, null)).EscalationId);
    }

    // A reply to the customer doesn't decide what the agent asked permission for.
    [Fact]
    public async Task WaitingAsync_AfterTheTeamReplied_StillListsAnApproval()
    {
        Approval("apr_1", "t1", Now.UtcDateTime.AddMinutes(-30));
        _replies[(WhatsApp, "2348012345678")] = Now.UtcDateTime.AddMinutes(-10);

        Assert.Equal("apr_1", Assert.Single(await _sut.WaitingAsync(Owner, null)).ApprovalId);
    }

    [Fact]
    public async Task OpenQuestionsAsync_ReturnsThisChatsOpenQuestionsAsAgentQuestionMessages()
    {
        const string bayoChat = "whatsapp:2348099999999:2349011112222";
        _inbox[bayoChat] = (WhatsApp, "2348099999999");
        Escalation("esc_ada", "t1", Now.UtcDateTime.AddMinutes(-20), conversation: AdaChat);
        Approval("apr_ada", "t2", Now.UtcDateTime.AddMinutes(-15));
        Escalation("esc_bayo", "t3", Now.UtcDateTime.AddMinutes(-10), conversation: bayoChat);

        IReadOnlyList<ConversationMessage> questions = await _sut.OpenQuestionsAsync(Owner, null, WhatsApp, "2348012345678");

        ConversationMessage question = Assert.Single(questions);
        Assert.Equal("esc_ada", question.Id);
        Assert.Equal("ai_question", question.Kind);
        Assert.Equal("No policy for exchanges", question.Text);
        Assert.Equal(Now.UtcDateTime.AddMinutes(-20), question.ReceivedAt);
    }

    [Fact]
    public async Task OpenQuestionsAsync_ForATeammateWhoCannotOpenTheChannel_IsEmpty()
    {
        Escalation("esc_ada", "t1", Now.UtcDateTime.AddMinutes(-20), conversation: AdaChat);

        Assert.Empty(await _sut.OpenQuestionsAsync(Owner, new HashSet<Guid> { Instagram }, WhatsApp, "2348012345678"));
    }

    [Fact]
    public async Task CloseQuestionAsync_ClosesTheQuestionForWhoeverClosedItAndSaysSoInTheChat()
    {
        AgentActivityRecord question = Escalation("esc_1", "t1", Now.UtcDateTime.AddMinutes(-20), conversation: AdaChat);

        await _sut.CloseQuestionAsync(Owner, null, WhatsApp, "2348012345678", "esc_1", "user-ngozi");

        Assert.Equal(Now.UtcDateTime, question.ResolvedAt);
        Assert.Equal("done", question.Resolution);
        Assert.Equal("user-ngozi", question.ResolvedBy);
        Assert.Empty(await _sut.WaitingAsync(Owner, null));
        _conversations.Verify(c => c.WriteSystemEventAsync(Owner, WhatsApp, "2348012345678",
            "A team member marked the agent's question as done.", It.IsAny<CancellationToken>()), Times.Once);
    }

    // Another workspace's question, one in another chat, one already answered and one that never existed all answer
    // the same, and nothing is changed or written.
    [Theory]
    [InlineData("esc_theirs")]
    [InlineData("esc_bayo")]
    [InlineData("esc_closed")]
    [InlineData("esc_never")]
    public async Task CloseQuestionAsync_ForAQuestionNotOpenInThisChat_ThrowsTheSameNotFoundAndChangesNothing(string questionId)
    {
        const string bayoChat = "whatsapp:2348099999999:2349011112222";
        _inbox[bayoChat] = (WhatsApp, "2348099999999");
        Escalation("esc_theirs", "t9", Now.UtcDateTime.AddMinutes(-5), conversation: AdaChat, owner: "someone-else");
        Escalation("esc_bayo", "t3", Now.UtcDateTime.AddMinutes(-5), conversation: bayoChat);
        Escalation("esc_closed", "t4", Now.UtcDateTime.AddMinutes(-50), conversation: AdaChat, resolvedAt: Now.UtcDateTime.AddMinutes(-40));

        NotFoundException error = await Assert.ThrowsAsync<NotFoundException>(() =>
            _sut.CloseQuestionAsync(Owner, null, WhatsApp, "2348012345678", questionId, "user-ngozi"));

        Assert.Equal("Question not found.", error.Message);
        Assert.Null(_activity.Records.Single(r => r.EscalationId == "esc_theirs").ResolvedAt);
        Assert.Null(_activity.Records.Single(r => r.EscalationId == "esc_bayo").ResolvedAt);
        _conversations.Verify(c => c.WriteSystemEventAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CloseQuestionAsync_OnAChannelTheTeammateCannotOpen_ThrowsNotFound()
    {
        AgentActivityRecord question = Escalation("esc_1", "t1", Now.UtcDateTime.AddMinutes(-20), conversation: AdaChat);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _sut.CloseQuestionAsync(Owner, new HashSet<Guid> { Instagram }, WhatsApp, "2348012345678", "esc_1", "user-ngozi"));

        Assert.Null(question.ResolvedAt);
    }

    [Fact]
    public async Task CloseQuestionAsync_WhenTheChatLineCannotBeWritten_StillClosesTheQuestion()
    {
        AgentActivityRecord question = Escalation("esc_1", "t1", Now.UtcDateTime.AddMinutes(-20), conversation: AdaChat);
        _conversations.Setup(c => c.WriteSystemEventAsync(Owner, WhatsApp, "2348012345678", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("mongo"));

        await _sut.CloseQuestionAsync(Owner, null, WhatsApp, "2348012345678", "esc_1", "user-ngozi");

        Assert.Equal("done", question.Resolution);
    }

    [Fact]
    public async Task OpenByConversationAsync_WithNothingOpen_IsEmptyAndReadsNoConversations()
    {
        Approval("apr_old", "t0", Now.UtcDateTime.AddHours(-3), resolvedAt: Now.UtcDateTime.AddHours(-2));

        Assert.Empty(await _sut.OpenByConversationAsync(Owner, null));
        _conversations.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DecideApprovalAsync_ForwardsTheDecisionAndRecordsIt()
    {
        Approval("apr_1", "t1", Now.UtcDateTime.AddMinutes(-10));
        Escalation("esc_2", "t1", Now.UtcDateTime.AddMinutes(-1), approval: "apr_1");
        _orchestrator.Setup(o => o.DecideApprovalAsync(Owner, "apr_1", It.IsAny<ApprovalDecision>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApprovalDecisionResult("t1", "completed", "Your refund is on its way."));

        ApprovalDecisionResponse response = await _sut.DecideApprovalAsync(Owner, "apr_1", new ApprovalDecisionRequest("Approved", "Refund in full"));

        Assert.Equal("approved", response.Decision);
        Assert.Equal("Your refund is on its way.", response.Reply);
        _orchestrator.Verify(o => o.DecideApprovalAsync(Owner, "apr_1",
            It.Is<ApprovalDecision>(d => d.Decision == "approved" && d.DecidedBy == Owner && d.Instruction == "Refund in full"),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.All(_activity.Records, r =>
        {
            Assert.Equal(Now.UtcDateTime, r.ResolvedAt);
            Assert.Equal("approved", r.Resolution);
            Assert.Equal(Owner, r.ResolvedBy);
        });
        Assert.Empty(await _sut.WaitingAsync(Owner, null));
    }

    [Fact]
    public async Task DecideApprovalAsync_ForAnApprovalThatIsNotTheOwners_ThrowsNotFoundWithoutCallingTheOrchestrator()
    {
        Approval("apr_theirs", "t9", Now.UtcDateTime, owner: "someone-else");

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.DecideApprovalAsync(Owner, "apr_theirs", new ApprovalDecisionRequest("approved", null)));

        _orchestrator.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DecideApprovalAsync_AlreadyDecided_ThrowsNotFound()
    {
        Approval("apr_1", "t1", Now.UtcDateTime.AddHours(-1), resolvedAt: Now.UtcDateTime.AddMinutes(-30));

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.DecideApprovalAsync(Owner, "apr_1", new ApprovalDecisionRequest("denied", null)));
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("")]
    public async Task DecideApprovalAsync_WithADecisionThatIsNeither_ThrowsValidation(string decision)
    {
        Approval("apr_1", "t1", Now.UtcDateTime);

        await Assert.ThrowsAsync<ValidationException>(() => _sut.DecideApprovalAsync(Owner, "apr_1", new ApprovalDecisionRequest(decision, null)));

        _orchestrator.VerifyNoOtherCalls();
    }

    // The orchestrator did not take the decision, so nothing is marked decided here either.
    [Fact]
    public async Task DecideApprovalAsync_WhenTheOrchestratorIsDown_LeavesTheApprovalWaiting()
    {
        Approval("apr_1", "t1", Now.UtcDateTime);
        _orchestrator.Setup(o => o.DecideApprovalAsync(Owner, "apr_1", It.IsAny<ApprovalDecision>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ServiceUnavailableException("down", new TimeoutException()));

        await Assert.ThrowsAsync<ServiceUnavailableException>(() => _sut.DecideApprovalAsync(Owner, "apr_1", new ApprovalDecisionRequest("approved", null)));

        Assert.Single(await _sut.WaitingAsync(Owner, null));
    }
}
