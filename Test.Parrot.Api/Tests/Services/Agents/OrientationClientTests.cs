using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Parrot.Application.Agents;
using Parrot.Application.Auth;
using Parrot.Application.Integrations;
using Parrot.Domain.Exceptions;
using Parrot.Infrastructure.Agents;

namespace Test.Parrot.Api.Tests.Services.Agents;

// Talking to the AI by voice: Parrot asks the orientation service for a signed ElevenLabs link,
// for the signed-in owner, with a service token minted for them.
public class OrientationClientTests
{
    private const string Owner = "owner-1";

    private const string Session = """
        {"signedUrl":"wss://api.elevenlabs.io/v1/convai/conversation?agent_id=a&conversation_signature=s",
         "dynamicVariables":{"tenant_id":"owner-1","secret__voice_session_token":"t"},
         "firstMessage":"What do you sell?","expiresIn":300}
        """;

    private static (OrientationClient Client, Handler Handler) Create(HttpStatusCode status, string body, bool enabled = true)
    {
        Handler handler = new(status, body);
        HttpClient http = new(handler) { BaseAddress = new Uri("https://orientation.example/") };
        Mock<IServiceTokenFactory> tokens = new();
        tokens.Setup(t => t.Mint(Owner)).Returns("service-token");
        OrientationClient client = new(http, tokens.Object,
            Options.Create(new OmniAiSettings { Enabled = enabled, OrientationUrl = "https://orientation.example" }),
            NullLogger<OrientationClient>.Instance);
        return (client, handler);
    }

    [Fact]
    public async Task AVoiceSession_IsTheSignedLink_AskedForWithTheOwnersServiceToken()
    {
        (OrientationClient client, Handler handler) = Create(HttpStatusCode.OK, Session);

        OrientationVoiceSession session = await client.StartVoiceSessionAsync(Owner);

        Assert.StartsWith("wss://api.elevenlabs.io/", session.SignedUrl);
        Assert.Equal("owner-1", session.DynamicVariables["tenant_id"]);
        Assert.Equal("t", session.DynamicVariables["secret__voice_session_token"]);
        Assert.Equal("What do you sell?", session.FirstMessage);
        Assert.Equal(300, session.ExpiresIn);
        Assert.Equal("https://orientation.example/api/v1/orientation/voice/session", handler.Url);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("Bearer service-token", handler.Authorization);
    }

    [Fact]
    public async Task TooManyStarts_SaysToWait()
    {
        (OrientationClient client, _) = Create(HttpStatusCode.TooManyRequests, """{"detail":"Voice session start limit exceeded"}""");

        ConflictException error = await Assert.ThrowsAsync<ConflictException>(() => client.StartVoiceSessionAsync(Owner));

        Assert.Contains("Wait a minute", error.Message);
    }

    [Fact]
    public async Task TooManyStarts_SaysHowLongToWait()
    {
        Handler handler = new(HttpStatusCode.TooManyRequests, """{"detail":"Voice session start limit exceeded"}""") { RetryAfterSeconds = 60 };
        HttpClient http = new(handler) { BaseAddress = new Uri("https://orientation.example/") };
        Mock<IServiceTokenFactory> tokens = new();
        tokens.Setup(t => t.Mint(Owner)).Returns("service-token");
        OrientationClient client = new(http, tokens.Object,
            Options.Create(new OmniAiSettings { Enabled = true, OrientationUrl = "https://orientation.example" }), NullLogger<OrientationClient>.Instance);

        ConflictException error = await Assert.ThrowsAsync<ConflictException>(() => client.StartVoiceSessionAsync(Owner));

        Assert.Contains("Wait 60 seconds", error.Message);
    }

    [Fact]
    public async Task ABusyService_KeepsItsWords()
    {
        (OrientationClient client, _) = Create(HttpStatusCode.ServiceUnavailable, """{"detail":"Voice session setup is busy"}""");

        ServiceUnavailableException error = await Assert.ThrowsAsync<ServiceUnavailableException>(() => client.StartVoiceSessionAsync(Owner));

        Assert.Equal("Voice session setup is busy", error.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, """{"detail":"Voice provider unavailable"}""")]
    [InlineData(HttpStatusCode.OK, """{"signedUrl":"https://not-a-socket.example","dynamicVariables":{},"firstMessage":"","expiresIn":300}""")]
    [InlineData(HttpStatusCode.OK, "<html>Not the API</html>")]
    [InlineData(HttpStatusCode.OK, """{"signedUrl":"wss://api.elevenlabs.io/x","dynamicVariables":{"tenant_id":"owner-1"},"firstMessage":"Hi","expiresIn":300}""")]
    [InlineData(HttpStatusCode.OK, """{"signedUrl":"wss://api.elevenlabs.io/x","dynamicVariables":{"tenant_id":"owner-1","secret__voice_session_token":"t"},"firstMessage":"","expiresIn":300}""")]
    [InlineData(HttpStatusCode.Unauthorized, """{"detail":"Invalid token"}""")]
    public async Task AnythingElse_IsAPlainTryAgain(HttpStatusCode status, string body)
    {
        (OrientationClient client, _) = Create(status, body);

        ServiceUnavailableException error = await Assert.ThrowsAsync<ServiceUnavailableException>(() => client.StartVoiceSessionAsync(Owner));

        Assert.Equal("Voice chat with the AI could not be started. Try again in a moment.", error.Message);
    }

    [Fact]
    public async Task WithTheAiSwitchedOff_NothingIsCalled()
    {
        (OrientationClient client, Handler handler) = Create(HttpStatusCode.OK, Session, enabled: false);

        await Assert.ThrowsAsync<ServiceUnavailableException>(() => client.StartVoiceSessionAsync(Owner));
        Assert.Equal(0, handler.Calls);
    }

    private sealed class Handler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Url { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? Authorization { get; private set; }
        public int? RetryAfterSeconds { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Url = request.RequestUri!.AbsoluteUri;
            Method = request.Method;
            Authorization = request.Headers.Authorization?.ToString();
            HttpResponseMessage response = new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (RetryAfterSeconds is { } seconds) response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
            return Task.FromResult(response);
        }
    }
}
