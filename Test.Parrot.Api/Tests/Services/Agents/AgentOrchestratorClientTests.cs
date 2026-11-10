using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Parrot.Application.Agents;
using Parrot.Application.Auth;
using Parrot.Application.Integrations;
using Parrot.Domain.Exceptions;
using Parrot.Infrastructure.Agents;

namespace Test.Parrot.Api.Tests.Services.Agents;

public class AgentOrchestratorClientTests
{
    private const string Owner = "owner-1";

    private static (AgentOrchestratorClient Client, FakeHandler Handler) Create(HttpStatusCode status, string body, bool enabled = true)
    {
        FakeHandler handler = new(status, body);
        HttpClient http = new(handler) { BaseAddress = new Uri("http://omni-ai:8000/") };
        Mock<IServiceTokenFactory> tokens = new();
        tokens.Setup(t => t.MintForAiService(Owner)).Returns("service-token");
        AgentOrchestratorClient client = new(
            http, tokens.Object, Options.Create(new OmniAiSettings { Enabled = enabled, BaseUrl = "http://omni-ai:8000" }), NullLogger<AgentOrchestratorClient>.Instance);
        return (client, handler);
    }

    [Fact]
    public async Task DecideApprovalAsync_PostsTheDecisionInTheOrchestratorsShapeWithAServiceToken()
    {
        (AgentOrchestratorClient client, FakeHandler handler) = Create(HttpStatusCode.OK, """{"turn_id":"turn_1","status":"completed","reply":"Done."}""");

        ApprovalDecisionResult result = await client.DecideApprovalAsync(Owner, "apr 1", new ApprovalDecision("approved", Owner, "Refund in full"));

        Assert.Equal("turn_1", result.TurnId);
        Assert.Equal("completed", result.Status);
        Assert.Equal("Done.", result.Reply);
        Assert.Equal("http://omni-ai:8000/api/v1/agent/approvals/apr%201", handler.Url);
        Assert.Equal("Bearer service-token", handler.Authorization);
        using JsonDocument json = JsonDocument.Parse(handler.Payload!);
        Assert.Equal("approved", json.RootElement.GetProperty("decision").GetString());
        Assert.Equal(Owner, json.RootElement.GetProperty("decided_by").GetString());
        Assert.Equal("Refund in full", json.RootElement.GetProperty("instruction").GetString());
    }

    [Fact]
    public async Task DecideApprovalAsync_ServerError_ThrowsServiceUnavailableWithoutTheBody()
    {
        (AgentOrchestratorClient client, _) = Create(HttpStatusCode.InternalServerError, """{"detail":"Traceback: secret internals"}""");

        ServiceUnavailableException ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => client.DecideApprovalAsync(Owner, "apr_1", new ApprovalDecision("approved", Owner, null)));

        Assert.DoesNotContain("secret internals", ex.Message);
        Assert.DoesNotContain("secret internals", ex.InnerException?.Message);
    }

    [Fact]
    public async Task DecideApprovalAsync_NotFound_ThrowsNotFound()
    {
        (AgentOrchestratorClient client, _) = Create(HttpStatusCode.NotFound, "{}");

        await Assert.ThrowsAsync<NotFoundException>(() => client.DecideApprovalAsync(Owner, "apr_1", new ApprovalDecision("approved", Owner, null)));
    }

    [Fact]
    public async Task DecideApprovalAsync_Conflict_ThrowsConflict()
    {
        (AgentOrchestratorClient client, _) = Create(HttpStatusCode.Conflict, "{}");

        await Assert.ThrowsAsync<ConflictException>(() => client.DecideApprovalAsync(Owner, "apr_1", new ApprovalDecision("approved", Owner, null)));
    }

    [Fact]
    public async Task DecideApprovalAsync_UnreadableAnswer_ThrowsServiceUnavailable()
    {
        (AgentOrchestratorClient client, _) = Create(HttpStatusCode.OK, "<html>not json</html>");

        await Assert.ThrowsAsync<ServiceUnavailableException>(() => client.DecideApprovalAsync(Owner, "apr_1", new ApprovalDecision("approved", Owner, null)));
    }

    [Fact]
    public async Task AnyCall_WhenTheOrchestratorIsOff_ThrowsServiceUnavailableWithoutCalling()
    {
        (AgentOrchestratorClient client, FakeHandler handler) = Create(HttpStatusCode.OK, "{}", enabled: false);

        await Assert.ThrowsAsync<ServiceUnavailableException>(() => client.GetRegistryAsync(Owner));

        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task GetTraceAsync_ReadsTheTraceAsItIs()
    {
        (AgentOrchestratorClient client, FakeHandler handler) = Create(HttpStatusCode.OK, """{"trace_id":"abc","spans":[{"name":"plan"}]}""");

        JsonElement trace = await client.GetTraceAsync(Owner, "abc");

        Assert.Equal("abc", trace.GetProperty("trace_id").GetString());
        Assert.Equal("http://omni-ai:8000/api/v1/agent/traces/abc", handler.Url);
        Assert.Equal(HttpMethod.Get, handler.Method);
    }

    [Fact]
    public async Task RunTestTurnAsync_SendsAWebChannelMessageForTheOwner()
    {
        (AgentOrchestratorClient client, FakeHandler handler) = Create(HttpStatusCode.OK, """{"turn_id":"turn_7","status":"completed","decision":"PERMIT","reply":"Hello!","confidence":0.8}""");

        TestTurnResult result = await client.RunTestTurnAsync(Owner, new TestTurnRequest("web:owner-1:test", "tester", "Do you deliver?"));

        Assert.Equal("PERMIT", result.Decision);
        Assert.Equal(0.8, result.Confidence);
        Assert.Equal("http://omni-ai:8000/api/v1/agent/turn", handler.Url);
        using JsonDocument json = JsonDocument.Parse(handler.Payload!);
        Assert.Equal("web", json.RootElement.GetProperty("channel").GetString());
        Assert.Equal(Owner, json.RootElement.GetProperty("tenant_id").GetString());
        Assert.Equal("Do you deliver?", json.RootElement.GetProperty("text").GetString());
        Assert.StartsWith("web:", json.RootElement.GetProperty("message_id").GetString());
    }

    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Url { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? Authorization { get; private set; }
        public string? Payload { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            // AbsoluteUri keeps the escaping the orchestrator will see; ToString() would undo it.
            Url = request.RequestUri!.AbsoluteUri;
            Method = request.Method;
            Authorization = request.Headers.Authorization?.ToString();
            Payload = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
