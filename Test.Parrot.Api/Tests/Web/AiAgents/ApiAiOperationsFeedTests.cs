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
}
