using System.Text.Json;
using Parrot.Application.Contracts.Conversations;
using Parrot.Application.Conversations;
using Parrot.Web.Components.Pages.Dashboard.Conversations;
using Parrot.Web.Services;

namespace Test.Parrot.Api.Tests.Web.Conversations;

// The inbox list row as the API sends it, read by the web app the way ConversationClient reads it.
public sealed class ConversationsStateTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly Guid Connection = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static InboxConversationDto ThroughTheWire(ConversationSummary row) =>
        JsonSerializer.Deserialize<InboxConversationDto>(JsonSerializer.Serialize(row, Web), Web)!;

    private static ConversationSummary Row() => new(Connection, "2348012345678", "Ada", "Do you deliver to Ibadan?", DateTime.UtcNow);

    [Fact]
    public void Apply_ARowWithAnOpenQuestion_ShowsItNeedsAnAnswerAndWhichAgentAsked()
    {
        ConversationsState state = new();

        state.Apply([ThroughTheWire(Row() with { NeedsAnswer = true, AiAgent = new ConversationAgentRef("agent-nova", "Nova") })]);

        ConversationItem item = Assert.Single(state.Conversations);
        Assert.Equal($"{Connection}:2348012345678", item.Id);
        Assert.True(item.NeedsAnswer);
        Assert.Equal("agent-nova", item.AiAgent?.Id);
        Assert.Equal("Nova", item.AiAgent?.Name);
    }

    // The server holds the approval open, so it shows even when no agent covers the channel any more.
    [Fact]
    public void Apply_ARowWithAPendingApproval_KeepsItWhateverTheAiState()
    {
        ConversationsState state = new();

        state.Apply([ThroughTheWire(Row() with { PendingApproval = new ConversationApproval("apr_1", "payments.refund", "Refund above the limit") })]);

        ConversationItem item = Assert.Single(state.Conversations);
        Assert.Null(item.AiState);
        Assert.Equal(new PendingApprovalInfo("apr_1", "payments.refund", "Refund above the limit"), item.PendingApproval);
    }

    // Once the question closes, the next list read takes the flag away.
    [Fact]
    public void Apply_ARowWhoseQuestionClosed_ClearsTheFlag()
    {
        ConversationsState state = new();
        state.Apply([ThroughTheWire(Row() with { NeedsAnswer = true })]);

        state.Apply([ThroughTheWire(Row())]);

        Assert.False(Assert.Single(state.Conversations).NeedsAnswer);
    }

    private static readonly DateTime At = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);

    private static InboxMessageDto Question(string id, int minute) => new(id, "Ada wants to know about Ibadan.", false, "open", At.AddMinutes(minute)) { Kind = "ai_question" };

    private static InboxMessageDto Reply(string status, int minute, string? kind = null) => new("reply", "We deliver to Ibadan.", true, status, At.AddMinutes(minute)) { Kind = kind };

    private static ConversationItem ThreadWith(params InboxMessageDto[] messages)
    {
        ConversationItem item = new() { Id = "chat", NeedsAnswer = true };
        ConversationsState.Merge(item, messages);
        return item;
    }

    // The newest page lists every question still open; one missing from it was closed elsewhere.
    [Fact]
    public void DropClosedQuestions_AQuestionTheNewestPageNoLongerHas_LeavesTheThread()
    {
        ConversationItem item = ThreadWith(Question("esc_1", 0), Question("esc_2", 1), new InboxMessageDto("m1", "Hello", false, "received", At));

        ConversationsState.DropClosedQuestions(item, [Question("esc_2", 1)]);

        Assert.Equal(["esc_2", "m1"], item.Messages.Select(m => m.Id).Order());
    }

    [Fact]
    public void SettleAgentQuestions_ASentTeamReply_AnswersTheQuestionsAskedBeforeIt()
    {
        ConversationItem item = ThreadWith(Question("esc_old", 0), Question("esc_new", 10));

        ConversationsState.SettleAgentQuestions(item, Reply("accepted", 5));

        Assert.True(item.Messages.Single(m => m.Id == "esc_old").Answered);
        Assert.False(item.Messages.Single(m => m.Id == "esc_new").Answered);
        Assert.True(item.NeedsAnswer);

        ConversationsState.SettleAgentQuestions(item, Reply("delivered", 15));

        Assert.False(item.NeedsAnswer);
    }

    // The server only counts a reply the channel took, and never an agent's own.
    [Theory]
    [InlineData("failed", null)]
    [InlineData("pending", null)]
    [InlineData("unknown", null)]
    [InlineData("accepted", "ai")]
    public void SettleAgentQuestions_AReplyThatDidNotGoOutOrIsTheAgents_LeavesTheQuestionOpen(string status, string? kind)
    {
        ConversationItem item = ThreadWith(Question("esc_1", 0));

        ConversationsState.SettleAgentQuestions(item, Reply(status, 5, kind));

        Assert.False(item.Messages.Single(m => m.Id == "esc_1").Answered);
        Assert.True(item.NeedsAnswer);
    }
}
