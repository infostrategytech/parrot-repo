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
}
