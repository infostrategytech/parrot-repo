using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Parrot.Application.Agents;
using Parrot.Web.Components.Pages.Dashboard.AiAgents;
using Parrot.Web.Features.Auth.Models;
using Parrot.Web.Features.Auth.State;
using Parrot.Web.Features.Dashboard.AiAgents;

namespace Test.Parrot.Api.Tests.Web.AiAgents;

// The Agents page reads the waiting questions as the API writes them, with the agent and conversation each was asked in.
public sealed class ApiAiOperationsFeedTests
{
    private sealed class Waiting(object body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(body, options: new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            });
    }

    [Fact]
    public async Task RefreshAsync_ReadsEachWaitingQuestionsAgentAndConversation()
    {
        AuthState auth = new();
        auth.SetUser(new AuthResponse { Token = "t", UserId = "u", Email = "a@b.c", ExpiresAt = DateTime.UtcNow.AddHours(1) });
        List<WaitingItemResponse> fromTheApi =
        [
            new("Nova", "Ada", "Do you deliver to Ibadan?", null, "esc_1", "P2", DateTime.UtcNow)
            {
                AgentId = "agent-nova", ConversationId = "33333333-3333-3333-3333-333333333333:2348012345678",
            },
        ];
        ApiAiOperationsFeed feed = new(new HttpClient(new Waiting(fromTheApi)) { BaseAddress = new Uri("http://parrot-api/") },
            auth, NullLogger<ApiAiOperationsFeed>.Instance);

        await feed.RefreshAsync();

        WaitingQuestion question = Assert.Single(feed.Waiting([]));
        Assert.Equal("agent-nova", question.AgentId);
        Assert.Equal("33333333-3333-3333-3333-333333333333:2348012345678", question.ConversationId);
        Assert.Equal("Do you deliver to Ibadan?", question.Question);
    }

    // Answers each figure by its path, and keeps every query it was sent.
    private sealed class Figures : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked.Add(request.RequestUri!.PathAndQuery);
            object body = request.RequestUri.AbsolutePath switch
            {
                "/api/ai-operations/today" when request.RequestUri.Query.Contains("agentId") => new AgentTodayResponse(1, 1, 0, 0, null),
                "/api/ai-operations/today" => new AgentTodayResponse(3, 1, 2, 1, 20),
                "/api/ai-operations/week" => new List<int> { 0, 0, 0, 0, 0, 0, 3 },
                _ => new List<RecentConversationResponse>(),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(body, options: new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            });
        }
    }

    // A reply two agents answered in counts for each of them, so the page's totals are the workspace's own figure.
    [Fact]
    public async Task WorkspaceToday_ReadsTheWorkspacesFigureNotAnAgents()
    {
        AuthState auth = new();
        auth.SetUser(new AuthResponse { Token = "t", UserId = "u", Email = "a@b.c", ExpiresAt = DateTime.UtcNow.AddHours(1) });
        Figures api = new();
        ApiAiOperationsFeed feed = new(new HttpClient(api) { BaseAddress = new Uri("http://parrot-api/") }, auth, NullLogger<ApiAiOperationsFeed>.Instance);
        TaskCompletionSource loaded = new();
        feed.Changed += () => loaded.TrySetResult();

        Assert.Equal(AgentToday.None, feed.WorkspaceToday());
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new AgentToday(3, 1, 2, 1, 20), feed.WorkspaceToday());
        Assert.Contains("/api/ai-operations/today?", api.Asked);
        Assert.Contains("/api/ai-operations/recent?limit=20", api.Asked);
        Assert.DoesNotContain(api.Asked, path => path.Contains("agentId"));

        await feed.RefreshAsync();
        Assert.Equal(new AgentToday(3, 1, 2, 1, 20), feed.WorkspaceToday());
    }

    [Fact]
    public async Task Today_ForOneAgent_StillAsksForThatAgentOnTheChannel()
    {
        AuthState auth = new();
        auth.SetUser(new AuthResponse { Token = "t", UserId = "u", Email = "a@b.c", ExpiresAt = DateTime.UtcNow.AddHours(1) });
        Figures api = new();
        ApiAiOperationsFeed feed = new(new HttpClient(api) { BaseAddress = new Uri("http://parrot-api/") }, auth, NullLogger<ApiAiOperationsFeed>.Instance);
        TaskCompletionSource loaded = new();
        feed.Changed += () => loaded.TrySetResult();

        feed.Today(new AiAgent { Id = "agt_tobi", Name = "Tobi" }, "whatsapp");
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new AgentToday(1, 1, 0), feed.Today(new AiAgent { Id = "agt_tobi", Name = "Tobi" }, "whatsapp"));
        Assert.Contains("/api/ai-operations/today?agentId=agt_tobi&channel=whatsapp", api.Asked);
        Assert.Contains("/api/ai-operations/recent?agentId=agt_tobi&channel=whatsapp&limit=20", api.Asked);
    }
}
