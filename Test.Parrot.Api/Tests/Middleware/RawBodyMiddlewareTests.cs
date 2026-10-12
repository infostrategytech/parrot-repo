using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Parrot.Api.Middleware;

namespace Test.Parrot.Api.Tests.Middleware;

public class RawBodyMiddlewareTests
{
    // Captures what was logged so the diagnostics themselves can be asserted on. Facebook webhooks
    // previously produced none of these, which made a rejected delivery impossible to diagnose.
    private sealed class CapturingLogger : ILogger<RawBodyMiddleware>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    private static DefaultHttpContext CreateContext(string path, string query = "", string body = "{}")
    {
        DefaultHttpContext context = new();
        context.Request.Method = "POST";
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.ContentLength = body.Length;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<CapturingLogger> RunAsync(DefaultHttpContext context, int statusCode = 200, bool? logBodies = null)
    {
        CapturingLogger logger = new();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(logBodies is null ? [] : [new(RawBodyMiddleware.LogBodiesKey, logBodies.ToString())])
            .Build();
        RawBodyMiddleware middleware = new(ctx => { ctx.Response.StatusCode = statusCode; return Task.CompletedTask; }, logger, configuration);
        await middleware.InvokeAsync(context);
        return logger;
    }

    [Fact]
    public async Task FacebookWebhooksAreLoggedOnArrivalAndCompletion()
    {
        CapturingLogger logger = await RunAsync(
            CreateContext("/api/webhooks/facebook", "?ref=abc", """{"object":"page"}"""));

        Assert.Contains(logger.Messages, m => m.Contains("Facebook webhook received"));
        Assert.Contains(logger.Messages, m => m.Contains("Facebook webhook completed"));
    }

    // TikTok's connection reference is the last path segment, so the log line must not carry it.
    [Fact]
    public async Task ATikTokWebhookIsLoggedWithoutItsConnectionReference()
    {
        string reference = new('a', 64);

        CapturingLogger logger = await RunAsync(CreateContext($"/api/webhooks/tiktok/{reference}"));

        string line = Assert.Single(logger.Messages, m => m.Contains("TikTok webhook received"));
        Assert.DoesNotContain(reference, line);
        Assert.Contains("HasConnectionReference=True", line);
        Assert.DoesNotContain(logger.Messages, m => m.Contains(reference));
    }

    // Payloads carry what customers wrote, so they reach the logs only while
    // body logging is switched on to diagnose a provider.
    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task WebhookPayloadsAreNotLogged_UnlessSwitchedOn(bool? logBodies)
    {
        CapturingLogger logger = await RunAsync(
            CreateContext("/api/webhooks/whatsapp", "?ref=abc", """{"text":"my meter token is 1234"}"""), logBodies: logBodies);

        Assert.DoesNotContain(logger.Messages, m => m.Contains("my meter token"));
    }

    // Logged before the controller, so a body that then fails model binding is still seen.
    [Fact]
    public async Task WebhookPayloadsAreLogged_WhenSwitchedOn_EvenWhenRejected()
    {
        CapturingLogger logger = await RunAsync(
            CreateContext("/api/webhooks/whatsapp/v1", "?ref=abc", """{"contacts":[{"wa_id":"1"}]}"""), statusCode: 400, logBodies: true);

        Assert.Contains(logger.Messages, m => m.Contains("WhatsApp webhook body") && m.Contains("""{"contacts":[{"wa_id":"1"}]}"""));
    }

    [Fact]
    public async Task ALoggedPayload_IsCappedInLength()
    {
        string huge = "{\"x\":\"" + new string('a', 40_000) + "\"}";

        CapturingLogger logger = await RunAsync(CreateContext("/api/webhooks/facebook", body: huge), logBodies: true);

        string line = Assert.Single(logger.Messages, m => m.Contains("webhook body"));
        Assert.Contains("Truncated=True", line);
        Assert.True(line.Length < 17_000);
    }

    [Fact]
    public async Task OnlyProviderWebhookBodiesAreLogged()
    {
        CapturingLogger logger = await RunAsync(
            CreateContext("/api/webhooks/other", body: """{"secret":"x"}"""), logBodies: true);

        Assert.DoesNotContain(logger.Messages, m => m.Contains("secret"));
    }

    [Fact]
    public async Task WhatsAppWebhooksKeepTheirExistingDiagnostics()
    {
        CapturingLogger logger = await RunAsync(CreateContext("/api/webhooks/whatsapp", "?ref=abc"));

        Assert.Contains(logger.Messages, m => m.Contains("WhatsApp webhook received"));
        Assert.Contains(logger.Messages, m => m.Contains("WhatsApp webhook completed"));
    }

    [Fact]
    public async Task ArrivalLogRecordsWhetherTheReferenceAndSignatureWerePresent()
    {
        // The two values that separate "the provider never reached us properly" from "we rejected it".
        DefaultHttpContext withBoth = CreateContext("/api/webhooks/facebook", "?ref=abc");
        withBoth.Request.Headers["X-Hub-Signature-256"] = "sha256=deadbeef";
        CapturingLogger present = await RunAsync(withBoth);
        Assert.Contains(present.Messages,
            m => m.Contains("HasConnectionReference=True") && m.Contains("HasSignature=True"));

        CapturingLogger absent = await RunAsync(CreateContext("/api/webhooks/facebook"));
        Assert.Contains(absent.Messages,
            m => m.Contains("HasConnectionReference=False") && m.Contains("HasSignature=False"));
    }

    [Fact]
    public async Task ARejectedWebhookIsLoggedAsAWarningWithItsStatusCode()
    {
        CapturingLogger logger = await RunAsync(CreateContext("/api/webhooks/facebook"), statusCode: 403);
        Assert.Contains(logger.Messages, m => m.Contains("Facebook webhook completed") && m.Contains("StatusCode=403"));
    }

    [Fact]
    public async Task TheRawBodyIsStillCapturedAndRewoundForSignatureValidation()
    {
        DefaultHttpContext context = CreateContext("/api/webhooks/facebook", body: """{"object":"page"}""");
        await RunAsync(context);

        Assert.Equal("""{"object":"page"}""", context.Items["RawBody"]);
        Assert.Equal(0, context.Request.Body.Position);
    }

    [Fact]
    public async Task NonWebhookRequestsAreNotLogged()
    {
        CapturingLogger logger = await RunAsync(CreateContext("/api/conversations"));
        Assert.Empty(logger.Messages);
    }
}
