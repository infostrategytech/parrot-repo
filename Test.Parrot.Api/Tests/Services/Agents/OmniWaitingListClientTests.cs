using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Moq;
using Parrot.Application.Agents;
using Parrot.Application.Integrations;
using Parrot.Application.Logging;
using Parrot.Domain.Exceptions;
using Parrot.Infrastructure.Agents;

namespace Test.Parrot.Api.Tests.Services.Agents;

// The AI service's waiting list, called as the signed-in person: their token goes as it is, and what the service says
// back is read in its snake_case shape. Its refusals never reach a client.
public class OmniWaitingListClientTests
{
    private const string Token = "session-ngozi";

    private const string AdaItem = """
        {"id": "wait_ada", "status": "open", "kind": "approval", "agent_id": "agent-nova", "channel": "whatsapp",
         "connection_id": "33333333-3333-3333-3333-333333333333", "customer_ref": "2348012345678", "customer_name": "Ada",
         "conversation_id": "33333333-3333-3333-3333-333333333333:2348012345678", "conversation_key": "whatsapp:2348012345678:2349011112222",
         "question": "I want a refund", "summary": "Refund needs approval", "priority": "P1", "question_count": 2,
         "escalation_id": "esc_2", "turn_id": "turn_2", "opened_at": "2026-11-23T12:11:00Z", "last_asked_at": "2026-11-23T12:30:00Z",
         "approval": {"id": "apr_1", "tool": "orders.refund", "reason": "Refund 12,000 naira for order 1042", "status": "pending",
                      "requested_at": "2026-11-23T12:11:00Z", "expires_at": "2026-11-23T13:11:00Z"},
         "resolution": null, "resolved_by": null, "resolved_at": null}
        """;

    private readonly Mock<IAppLogger<OmniWaitingListClient>> _logger = new();

    private (OmniWaitingListClient Client, FakeHandler Handler) Create(bool enabled = true, params (HttpStatusCode Status, string Body)[] answers)
    {
        FakeHandler handler = new(answers);
        HttpClient http = new(handler) { BaseAddress = new Uri("http://omni-ai:8000/") };
        OmniWaitingListClient client = new(
            http, Options.Create(new OmniAiSettings { Enabled = enabled, BaseUrl = "http://omni-ai:8000" }), _logger.Object);
        return (client, handler);
    }

    private (OmniWaitingListClient Client, FakeHandler Handler) Create(params (HttpStatusCode Status, string Body)[] answers) => Create(true, answers);

    [Fact]
    public async Task ListOpenAsync_ReadsEveryPageAsThePerson()
    {
        (OmniWaitingListClient client, FakeHandler handler) = Create(
            (HttpStatusCode.OK, $$"""{"items": [{{AdaItem}}], "next_cursor": "c/2+"}"""),
            (HttpStatusCode.OK, $$"""{"items": [{{AdaItem.Replace("wait_ada", "wait_bola")}}], "next_cursor": null}"""));

        IReadOnlyList<WaitingListItem> items = await client.ListOpenAsync(Token);

        Assert.Equal(["wait_ada", "wait_bola"], items.Select(i => i.Id));
        Assert.Equal(
            ["http://omni-ai:8000/api/v1/ai-operations/waiting?limit=100", "http://omni-ai:8000/api/v1/ai-operations/waiting?limit=100&cursor=c%2F2%2B"],
            handler.Urls);
        Assert.All(handler.Authorizations, a => Assert.Equal($"Bearer {Token}", a));
    }

    [Fact]
    public async Task ListOpenAsync_ReadsTheWaitingItemShape()
    {
        (OmniWaitingListClient client, _) = Create((HttpStatusCode.OK, $$"""{"items": [{{AdaItem}}], "next_cursor": null}"""));

        WaitingListItem item = Assert.Single(await client.ListOpenAsync(Token));

        Assert.Equal("open", item.Status);
        Assert.Equal("approval", item.Kind);
        Assert.Equal("agent-nova", item.AgentId);
        Assert.Equal("2348012345678", item.CustomerRef);
        Assert.Equal("Ada", item.CustomerName);
        Assert.Equal("whatsapp:2348012345678:2349011112222", item.ConversationKey);
        Assert.Equal("Refund needs approval", item.Summary);
        Assert.Equal("P1", item.Priority);
        Assert.Equal(2, item.QuestionCount);
        Assert.Equal("esc_2", item.EscalationId);
        Assert.Equal(new DateTime(2026, 11, 23, 12, 11, 0, DateTimeKind.Utc), item.OpenedAt);
        Assert.Equal(new DateTime(2026, 11, 23, 12, 30, 0, DateTimeKind.Utc), item.LastAskedAt);
        Assert.Equal(new WaitingListApproval("apr_1", "orders.refund", "Refund 12,000 naira for order 1042", "pending"), item.Approval);
        Assert.Null(item.Resolution);
    }

    [Fact]
    public async Task ListOpenAsync_StopsAfterTheLastPageItReadsAtMost()
    {
        (HttpStatusCode, string)[] endless = Enumerable.Repeat((HttpStatusCode.OK, $$"""{"items": [{{AdaItem}}], "next_cursor": "more"}"""), 20).ToArray();
        (OmniWaitingListClient client, FakeHandler handler) = Create(endless);

        IReadOnlyList<WaitingListItem> items = await client.ListOpenAsync(Token);

        Assert.Equal(OmniWaitingListClient.MaxPages, handler.Urls.Count);
        Assert.Equal(OmniWaitingListClient.MaxPages, items.Count);
    }

    [Fact]
    public async Task ResolveAsync_PostsToTheItemAsThePersonAndReturnsItAsItNowIs()
    {
        string resolved = AdaItem.Replace("\"status\": \"open\"", "\"status\": \"resolved\"")
            .Replace("\"resolution\": null, \"resolved_by\": null", "\"resolution\": \"handled\", \"resolved_by\": \"user-ngozi\"");
        (OmniWaitingListClient client, FakeHandler handler) = Create((HttpStatusCode.OK, resolved));

        WaitingListItem item = await client.ResolveAsync(Token, "wait_ada");

        Assert.Equal(HttpMethod.Post, handler.Methods.Single());
        Assert.Equal("http://omni-ai:8000/api/v1/ai-operations/waiting/wait_ada/resolve", handler.Urls.Single());
        Assert.Equal($"Bearer {Token}", handler.Authorizations.Single());
        Assert.Equal("handled", item.Resolution);
        Assert.Equal("user-ngozi", item.ResolvedBy);
    }

    // No such item in the person's workspace, which is also the answer for another workspace's.
    [Fact]
    public async Task GetAndResolve_WhenTheServiceHasNoSuchItem_ThrowNotFound()
    {
        const string missing = """{"success": false, "errorCode": "WAITING_ITEM_NOT_FOUND", "message": "nothing with this id is waiting in this workspace", "traceId": "t1"}""";
        (OmniWaitingListClient client, _) = Create((HttpStatusCode.NotFound, missing), (HttpStatusCode.NotFound, missing));

        NotFoundException read = await Assert.ThrowsAsync<NotFoundException>(() => client.GetAsync(Token, "wait_theirs"));
        NotFoundException resolve = await Assert.ThrowsAsync<NotFoundException>(() => client.ResolveAsync(Token, "wait_theirs"));

        Assert.Equal("Question not found.", read.Message);
        Assert.Equal(read.Message, resolve.Message);
    }

    // The shape the AI team agreed: the person's own words, whether the agent replies, and whether it keeps them.
    [Fact]
    public async Task AnswerAsync_PostsTheAnswerToTheItemAsThePersonInTheServicesShape()
    {
        (OmniWaitingListClient client, FakeHandler handler) = Create((HttpStatusCode.OK, """{"status": "accepted"}"""));

        await client.AnswerAsync(Token, "wait/ada", new WaitingListAnswer("Yes, students get 10% off their first purchase.", ReplyToCustomer: false, SaveForNextTime: true));

        Assert.Equal(HttpMethod.Post, handler.Methods.Single());
        Assert.Equal("http://omni-ai:8000/api/v1/ai-operations/waiting/wait%2Fada/answer", handler.Urls.Single());
        Assert.Equal($"Bearer {Token}", handler.Authorizations.Single());
        using JsonDocument body = JsonDocument.Parse(handler.Bodies.Single()!);
        Assert.Equal(
            ["answer", "reply_to_customer", "save_for_next_time"],
            body.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Yes, students get 10% off their first purchase.", body.RootElement.GetProperty("answer").GetString());
        Assert.False(body.RootElement.GetProperty("reply_to_customer").GetBoolean());
        Assert.True(body.RootElement.GetProperty("save_for_next_time").GetBoolean());
    }

    [Fact]
    public async Task AnswerAsync_WhenTheServiceHasNoSuchItem_ThrowsTheSameNotFound()
    {
        (OmniWaitingListClient client, _) = Create((HttpStatusCode.NotFound,
            """{"success": false, "errorCode": "WAITING_ITEM_NOT_FOUND", "message": "nothing with this id is waiting in this workspace", "traceId": "t1"}"""));

        NotFoundException error = await Assert.ThrowsAsync<NotFoundException>(() =>
            client.AnswerAsync(Token, "wait_theirs", new WaitingListAnswer("Yes", ReplyToCustomer: true, SaveForNextTime: false)));

        Assert.Equal("Question not found.", error.Message);
    }

    [Fact]
    public async Task AnswerAsync_WhenTheServiceRefuses_ThrowsServiceUnavailableWithoutItsBody()
    {
        (OmniWaitingListClient client, _) = Create((HttpStatusCode.Conflict,
            """{"success": false, "errorCode": "CONVERSATION_BUSY", "message": "secret internals", "traceId": "t5"}"""));

        ServiceUnavailableException error = await Assert.ThrowsAsync<ServiceUnavailableException>(() =>
            client.AnswerAsync(Token, "wait_ada", new WaitingListAnswer("Yes", ReplyToCustomer: true, SaveForNextTime: false)));

        Assert.DoesNotContain("secret internals", error.Message);
        Assert.DoesNotContain("secret internals", error.InnerException?.Message);
    }

    // A live failure names the call (which item, which action) and the service's own trace id, to look up on its side.
    [Fact]
    public async Task AnswerAsync_WhenTheServiceRefuses_LogsTheItemTheActionAndTheServicesTraceId()
    {
        (OmniWaitingListClient client, _) = Create((HttpStatusCode.Conflict,
            """{"success": false, "errorCode": "CONVERSATION_BUSY", "message": "secret internals", "traceId": "omni-trace-7"}"""));

        await Assert.ThrowsAsync<ServiceUnavailableException>(() =>
            client.AnswerAsync(Token, "wait_ada", new WaitingListAnswer("Yes", ReplyToCustomer: true, SaveForNextTime: false)));

        _logger.Verify(l => l.LogWarning(
            It.Is<string>(m => m.StartsWith("waiting_list.failed ")),
            It.Is<object?[]>(args => args.Contains("api/v1/ai-operations/waiting/wait_ada/answer") && args.Contains(409)
                && args.Contains("CONVERSATION_BUSY") && args.Contains("omni-trace-7"))), Times.Once);
    }

    [Fact]
    public async Task ListOpenAsync_WhenTheServiceRefuses_LogsThePathWithoutItsQuery()
    {
        (OmniWaitingListClient client, _) = Create((HttpStatusCode.BadGateway, ""));

        await Assert.ThrowsAsync<ServiceUnavailableException>(() => client.ListOpenAsync(Token));

        _logger.Verify(l => l.LogWarning(
            It.Is<string>(m => m.StartsWith("waiting_list.failed ")),
            It.Is<object?[]>(args => args.Contains("api/v1/ai-operations/waiting") && args.Contains(502))), Times.Once);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, """{"success": false, "errorCode": "RATE_LIMITED", "message": "secret internals", "traceId": "t2"}""")]
    [InlineData(HttpStatusCode.ServiceUnavailable, """{"success": false, "errorCode": "AI_OPERATIONS_UNAVAILABLE", "message": "secret internals", "traceId": "t3"}""")]
    [InlineData(HttpStatusCode.Unauthorized, """{"success": false, "errorCode": "UNAUTHORIZED", "message": "secret internals", "traceId": "t4"}""")]
    [InlineData(HttpStatusCode.NotFound, "<html>secret internals</html>")]
    [InlineData(HttpStatusCode.OK, "<html>secret internals</html>")]
    public async Task AnyCall_WhenTheServiceRefusesOrAnswersOddly_ThrowsServiceUnavailableWithoutItsBody(HttpStatusCode status, string body)
    {
        (OmniWaitingListClient client, _) = Create((status, body));

        ServiceUnavailableException error = await Assert.ThrowsAsync<ServiceUnavailableException>(() => client.ListOpenAsync(Token));

        Assert.DoesNotContain("secret internals", error.Message);
        Assert.DoesNotContain("secret internals", error.InnerException?.Message);
    }

    [Fact]
    public async Task AnyCall_WhenTheServiceIsOff_ThrowsServiceUnavailableWithoutCalling()
    {
        (OmniWaitingListClient client, FakeHandler handler) = Create(enabled: false);

        await Assert.ThrowsAsync<ServiceUnavailableException>(() => client.ListOpenAsync(Token));

        Assert.Empty(handler.Urls);
    }

    private sealed class FakeHandler((HttpStatusCode Status, string Body)[] answers) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        public List<HttpMethod> Methods { get; } = [];
        public List<string?> Authorizations { get; } = [];
        public List<string?> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // AbsoluteUri keeps the escaping the service will see; ToString() would undo it.
            Urls.Add(request.RequestUri!.AbsoluteUri);
            Methods.Add(request.Method);
            Authorizations.Add(request.Headers.Authorization?.ToString());
            Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            (HttpStatusCode status, string body) = answers[Urls.Count - 1];
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
