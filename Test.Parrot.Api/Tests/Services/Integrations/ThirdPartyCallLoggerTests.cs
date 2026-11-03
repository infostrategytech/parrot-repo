using System.Net;
using Microsoft.Extensions.Logging;
using Parrot.Application.Integrations;

namespace Test.Parrot.Api.Tests.Services.Integrations;

// Every call to an outside service leaves one line saying where it went, what came back and how
// long it took, without the query string, where a cursor or token can travel.
public class ThirdPartyCallLoggerTests
{
    [Fact]
    public async Task A_call_is_logged_with_its_service_path_status_and_time_but_not_its_query()
    {
        CapturingLogger logger = new();
        HttpClient client = Client(logger, _ => new HttpResponseMessage(HttpStatusCode.OK));

        await client.GetAsync("api/v1/agents?cursor=secret-cursor");

        (LogLevel level, string message) = Assert.Single(logger.Lines);
        Assert.Equal(LogLevel.Information, level);
        Assert.StartsWith("third_party.call Service=Omni-AI Method=GET Path=/api/v1/agents Status=200 ElapsedMs=", message);
        Assert.DoesNotContain("secret-cursor", message);
    }

    [Fact]
    public async Task An_error_status_is_a_warning()
    {
        CapturingLogger logger = new();
        HttpClient client = Client(logger, _ => new HttpResponseMessage(HttpStatusCode.BadGateway));

        await client.PostAsync("api/v1/orientation/text/a1", null);

        (LogLevel level, string message) = Assert.Single(logger.Lines);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("Method=POST Path=/api/v1/orientation/text/a1 Status=502", message);
    }

    [Fact]
    public async Task An_unreachable_service_is_a_warning_saying_why()
    {
        CapturingLogger logger = new();
        HttpClient client = Client(logger, _ => throw new HttpRequestException("Connection refused"));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("api/v1/agents"));

        (LogLevel level, string message) = Assert.Single(logger.Lines);
        Assert.Equal(LogLevel.Warning, level);
        Assert.StartsWith("third_party.failed Service=Omni-AI Method=GET Path=/api/v1/agents", message);
        Assert.Contains("Error=HttpRequestException: Connection refused", message);
    }

    [Fact]
    public async Task A_failed_handshake_says_why_not_just_see_inner_exception()
    {
        CapturingLogger logger = new();
        HttpClient client = Client(logger, _ => throw new HttpRequestException(
            "The SSL connection could not be established, see inner exception.",
            new System.Security.Authentication.AuthenticationException("The remote certificate is invalid because of errors in the certificate chain: UntrustedRoot")));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("api/v1/channels"));

        (_, string message) = Assert.Single(logger.Lines);
        Assert.EndsWith("Cause=AuthenticationException: The remote certificate is invalid because of errors in the certificate chain: UntrustedRoot", message);
    }

    private static HttpClient Client(CapturingLogger logger, Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new ThirdPartyCallLogger("Omni-AI", logger) { InnerHandler = new Responder(respond) })
        {
            BaseAddress = new Uri("http://omni-ai:8000/"),
        };

    private sealed class Responder(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class CapturingLogger : ILogger<ThirdPartyCallLogger>
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add((logLevel, formatter(state, exception)));
    }
}
