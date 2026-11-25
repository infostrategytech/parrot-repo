using System.Net;
using System.Text;
using System.Text.Json;
using Parrot.Application.Contracts.Conversations;
using Parrot.Web.Features.Auth.Models;
using Parrot.Web.Features.Auth.State;
using Parrot.Web.Services;

namespace Test.Parrot.Api.Tests.Web.Conversations;

// The Answer dialog sends the person's answer to the API's answer route for that question, and reads back whether the
// agent replies; a question closed elsewhere comes back as not found, with the API's own words.
public sealed class ConversationClientAnswerTests
{
    private static readonly Guid Connection = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static (ConversationClient Client, Recorder Recorder) Create(HttpStatusCode status, string body)
    {
        AuthState auth = new();
        auth.SetUser(new AuthResponse { Token = "t", UserId = "u", Email = "a@b.c", ExpiresAt = DateTime.UtcNow.AddHours(1) });
        Recorder recorder = new(status, body);
        return (new ConversationClient(new HttpClient(recorder) { BaseAddress = new Uri("http://parrot-api:8080/") }, auth), recorder);
    }

    [Fact]
    public async Task AnswerQuestionAsync_PostsTheAnswerToThatQuestionAndReadsWhetherTheAgentReplies()
    {
        (ConversationClient client, Recorder recorder) = Create(HttpStatusCode.OK, """{"agentReplies":false}""");

        QuestionAnswerResponse result = await client.AnswerQuestionAsync(Connection, "+2348012345678", "wait/1", "Yes, 10% off.", saveForNextTime: true, default);

        Assert.False(result.AgentReplies);
        Assert.Equal(HttpMethod.Post, recorder.Method);
        Assert.Equal(
            "http://parrot-api:8080/api/conversations/33333333-3333-3333-3333-333333333333/questions/wait%2F1/answer?sender=%2B2348012345678",
            recorder.Url);
        Assert.Equal("Bearer t", recorder.Authorization);
        using JsonDocument sent = JsonDocument.Parse(recorder.Body!);
        Assert.Equal("Yes, 10% off.", sent.RootElement.GetProperty("answer").GetString());
        Assert.True(sent.RootElement.GetProperty("saveForNextTime").GetBoolean());
    }

    [Fact]
    public async Task AnswerQuestionAsync_ForAQuestionNoLongerOpen_ThrowsNotFoundWithTheApisMessage()
    {
        (ConversationClient client, _) = Create(HttpStatusCode.NotFound, """{"message":"Question not found."}""");

        HttpRequestException error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.AnswerQuestionAsync(Connection, "2348012345678", "wait_1", "Yes", saveForNextTime: false, default));

        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        Assert.Equal("Question not found.", error.Message);
    }

    private sealed class Recorder(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? Url { get; private set; }
        public string? Authorization { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Url = request.RequestUri!.AbsoluteUri;
            Authorization = request.Headers.Authorization?.ToString();
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
