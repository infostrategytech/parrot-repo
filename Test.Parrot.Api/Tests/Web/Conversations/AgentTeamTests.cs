using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Parrot.SharedComponents.Services;
using Parrot.Web.Components.Pages.Dashboard.Conversations;
using Parrot.Web.Services;

namespace Test.Parrot.Api.Tests.Web.Conversations;

// The agents answering on a conversation's number, named together wherever the inbox says who is replying.
public sealed class AgentTeamTests
{
    [Theory]
    [InlineData(new[] { "Tolu" }, "Tolu")]
    [InlineData(new[] { "Tobi", "Kora" }, "Tobi and Kora")]
    [InlineData(new[] { "Nia", "Tobi", "Kora" }, "Nia and 2 others")]
    public void Names_SaysWhoAsTheScreensDo(string[] names, string expected) =>
        Assert.Equal(expected, AgentTeam.Names(names));

    [Fact]
    public async Task Row_WithSeveralAgentsOnItsNumber_SaysTheyAreReplyingAndShowsTheirFaces()
    {
        ConversationItem conversation = Conversation(AiState.Active, Nia, Tobi, Kora);

        string html = await Render(conversation, ["Nova", "Ember", "Coral"]);

        Assert.Contains("Nia and 2 others · Replying", html);
        Assert.Equal(3, CountOf(html, "agent-faces__face"));
    }

    [Fact]
    public async Task Row_WhenOneOfThemAsked_NamesTheAgentThatAsked()
    {
        ConversationItem conversation = Conversation(AiState.Active, Nia, Tobi, Kora);
        conversation.NeedsAnswer = true;
        conversation.AiAgent = Kora;

        string html = await Render(conversation, ["Nova", "Ember", "Coral"]);

        Assert.Contains("Kora · Needs your answer", html);
        Assert.DoesNotContain("agent-faces__face", html);
    }

    [Fact]
    public async Task Row_WithOneAgent_LooksAsItDidBefore()
    {
        ConversationItem conversation = Conversation(AiState.Paused, Tobi);

        string html = await Render(conversation, ["Ember"]);

        Assert.Contains("Tobi · Paused", html);
        Assert.DoesNotContain("agent-faces__face", html);
    }

    [Fact]
    public async Task AgentsInvolved_ListsEveryAgentAskedWithWhatItWasAskedAndAnswered()
    {
        IReadOnlyList<InboxReplyAgentDto> agents =
        [
            new("agt_tobi", "Tobi", "Nova", "Where is order OM-9821?", "OM-9821 left Ikeja this morning.", "answered"),
            new("agt_ada", "Ada", null, "Any discount on the next order?", "", "timed_out"),
            new("agt_ife", "Ife", null, "Is it insured?", "", "failed"),
        ];

        string html = await RenderList(agents, ["Nova", "Buzz", "Luna"]);

        Assert.Equal(3, CountOf(html, "reply-agents__agent\""));
        Assert.Contains("Where is order OM-9821?", html);
        Assert.Contains("OM-9821 left Ikeja this morning.", html);
        Assert.Contains("Didn't answer in time.", html);
        Assert.Contains("Didn't answer.", html);
        Assert.True(html.IndexOf("Tobi", StringComparison.Ordinal) < html.IndexOf("Ada", StringComparison.Ordinal), "the AI service's order");
    }

    private static async Task<string> RenderList(IReadOnlyList<InboxReplyAgentDto> agents, IReadOnlyList<string> looks)
    {
        await using ServiceProvider services = new ServiceCollection().AddLogging().AddSingleton<IJSRuntime>(new NoJs())
            .AddSingleton<AuroraInterop>().BuildServiceProvider();
        await using HtmlRenderer renderer = new(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            HtmlRootComponent root = await renderer.RenderComponentAsync<ReplyAgentsList>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Agents"] = agents,
                ["Looks"] = looks,
            }));
            return System.Net.WebUtility.HtmlDecode(root.ToHtmlString());
        });
    }

    [Fact]
    public void Look_ForAnAgentNotSetUpHere_IsTheLookTheAiServiceNamed()
    {
        Assert.Equal("Bolt", AgentLookup.Look("agt_gone", "Kora", [], "bolt"));
        // Not one of ours: the agent's own look from its name, as before.
        Assert.Equal(AgentLookup.Look("agt_gone", "Kora", []), AgentLookup.Look("agt_gone", "Kora", [], "super"));
    }

    [Fact]
    public async Task Reply_WrittenFromSeveralAgents_NamesThoseThatAnsweredAndShowsTheirFaces()
    {
        ConvMessage reply = AiReply(
            new InboxReplyAgentDto("agt_tobi", "Tobi", "Nova", "Where is it?", "It left Ikeja.", "answered"),
            new InboxReplyAgentDto("agt_kora", "Kora", "Bolt", "How much?", "N21,000.", "answered"),
            new InboxReplyAgentDto("agt_ada", "Ada", null, "Any discount?", "", "failed"));

        // One look per agent asked, in order: Ada's is for the list of who was asked.
        string html = await RenderReply(reply, ["Nova", "Bolt", "Buzz"]);

        Assert.Contains(">Tobi and Kora<", html);
        Assert.Equal(2, CountOf(html, "agent-faces__face"));
        Assert.Contains("aria-haspopup=\"dialog\"", html);
        Assert.Contains("Agents involved in this reply: Tobi, Kora and Ada", html);
    }

    [Fact]
    public async Task Reply_OneAgentAnswered_NamesThatAgentEvenIfTheChatsAgentIsAnother()
    {
        ConvMessage reply = AiReply(
            new InboxReplyAgentDto("agt_kora", "Kora", "Bolt", "How much?", "N21,000.", "answered"),
            new InboxReplyAgentDto("agt_ada", "Ada", null, "Any discount?", "", "timed_out"));

        string html = await RenderReply(reply, ["Bolt", "Buzz"]);

        Assert.Contains(">Kora<", html);
        Assert.DoesNotContain(">Tolu<", html);
        Assert.DoesNotContain("agent-faces__face", html);
    }

    [Fact]
    public async Task Reply_WithoutHelpers_IsTheChatsAgentAsBefore()
    {
        string html = await RenderReply(AiReply(), []);

        Assert.Contains(">Tolu<", html);
        Assert.DoesNotContain("agent-faces__face", html);
        Assert.DoesNotContain("Agents involved", html);
    }

    private static ConvMessage AiReply(params InboxReplyAgentDto[] agents) => new()
    {
        Id = "outgoing:w:r1", Text = "It left Ikeja, and the sneakers are N21,000.", IsOutgoing = true, Status = "read",
        Kind = MessageKind.Ai, Agents = agents,
    };

    private static async Task<string> RenderReply(ConvMessage reply, IReadOnlyList<string> looks)
    {
        await using ServiceProvider services = new ServiceCollection().AddLogging().AddSingleton<IJSRuntime>(new NoJs())
            .AddSingleton<AuroraInterop>().AddSingleton<ToastService>().BuildServiceProvider();
        await using HtmlRenderer renderer = new(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            HtmlRootComponent root = await renderer.RenderComponentAsync<ConversationMessageItem>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Message"] = reply,
                ["CustomerName"] = "Lebo Mokoena",
                ["CustomerSeed"] = "c1",
                // The chat's agent, as the timeline names AI replies today.
                ["AuthorName"] = "Tolu",
                ["AuthorSeed"] = "business",
                ["AgentLook"] = "Nova",
                ["ReplyLooks"] = looks,
            }));
            return System.Net.WebUtility.HtmlDecode(root.ToHtmlString());
        });
    }

    private static readonly InboxPersonDto Nia = new("agt_nia", "Nia");
    private static readonly InboxPersonDto Tobi = new("agt_tobi", "Tobi");
    private static readonly InboxPersonDto Kora = new("agt_kora", "Kora");

    private static ConversationItem Conversation(AiState state, params InboxPersonDto[] agents) => new()
    {
        Id = "c1",
        CustomerName = "Lebo Mokoena",
        LastMessage = "The latest update…",
        AiState = state,
        AiAgent = agents[0],
        ChannelAgent = agents[0],
        Agents = agents,
    };

    private static int CountOf(string html, string text) => (html.Length - html.Replace(text, "").Length) / text.Length;

    private static async Task<string> Render(ConversationItem conversation, IReadOnlyList<string> looks)
    {
        await using ServiceProvider services = new ServiceCollection().AddLogging().AddSingleton<IJSRuntime>(new NoJs()).AddSingleton<AuroraInterop>().BuildServiceProvider();
        await using HtmlRenderer renderer = new(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            HtmlRootComponent root = await renderer.RenderComponentAsync<ConversationRow>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Conversation"] = conversation,
                ["AgentLook"] = looks[0],
                ["TeamLooks"] = looks,
            }));
            // As a person reads it: the renderer escapes the "·" between who and what.
            return System.Net.WebUtility.HtmlDecode(root.ToHtmlString());
        });
    }

    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string id, CancellationToken ct, object?[]? args) => InvokeAsync<T>(id, args);
    }
}
