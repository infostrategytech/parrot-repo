using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Parrot.Application.BusinessSources;
using Parrot.Application.Integrations;
using Parrot.Infrastructure.BusinessSources;

namespace Test.Parrot.Api.Tests.BusinessSources;

// Reading a channel's profile from Meta: the access token never travels in the URL or lands in a log,
// and when Meta refuses, its own reason (and the id Meta support asks for) is in the log.
public class MetaProfileClientTests
{
    private const string Token = "EAAG-secret-token-123";

    [Fact]
    public async Task GetProfileAsync_SendsTheTokenInTheHeaderNeverTheUrl()
    {
        Meta meta = new();
        meta.Answer("/v25.0/15550001234", """{"verified_name":"Adire House","display_phone_number":"+234 803 123 4567"}""");
        meta.Answer("/v25.0/15550001234/whatsapp_business_profile", """{"data":[{"about":"Hand-dyed adire","email":"hello@adirehouse.ng"}]}""");
        ListLogger<WhatsAppProfileClient> log = new();

        WhatsAppProfileData? profile = await new WhatsAppProfileClient(meta.Factory(WhatsAppProfileClient.HttpClientName), Version(), log)
            .GetProfileAsync("15550001234", Token, CancellationToken.None);

        Assert.Equal("Adire House", profile!.VerifiedName);
        Assert.Equal(2, meta.Requests.Count);
        Assert.All(meta.Requests, request =>
        {
            Assert.Equal($"Bearer {Token}", request.Authorization);
            Assert.DoesNotContain(Token, request.Url);
            Assert.DoesNotContain("access_token", request.Url);
        });
        Assert.DoesNotContain(log.Lines, line => line.Contains(Token));
        // The business's number is recognisable but not whole.
        Assert.Contains(log.Lines, line => line.Contains("+234******4567"));
    }

    [Fact]
    public async Task GetProfileAsync_WhenMetaRefuses_LogsMetasReasonAndTraceIdWithoutTheToken()
    {
        Meta meta = new();
        meta.Answer("/v25.0/15550001234",
            """{"error":{"message":"Error validating access token: Session has expired.","type":"OAuthException","code":190,"error_subcode":463,"fbtrace_id":"AbCdEf123"}}""",
            HttpStatusCode.BadRequest);
        ListLogger<WhatsAppProfileClient> log = new();

        WhatsAppProfileData? profile = await new WhatsAppProfileClient(meta.Factory(WhatsAppProfileClient.HttpClientName), Version(), log)
            .GetProfileAsync("15550001234", Token, CancellationToken.None);

        Assert.Null(profile);
        string refused = Assert.Single(log.Lines, line => line.StartsWith("meta.read_refused"));
        Assert.Contains("Status=400", refused);
        Assert.Contains("MetaCode=190", refused);
        Assert.Contains("MetaSubcode=463", refused);
        Assert.Contains("FbTraceId=AbCdEf123", refused);
        Assert.Contains("Session has expired", refused);
        Assert.DoesNotContain(log.Lines, line => line.Contains(Token));
    }

    // Meta repeats a token it can't read in its own message ("Malformed access token EAAG…").
    [Fact]
    public async Task GetProfileAsync_WhenMetaRepeatsTheTokenInItsMessage_TheLogMasksIt()
    {
        Meta meta = new();
        meta.Answer("/v25.0/15550001234",
            $$$"""{"error":{"message":"Malformed access token {{{Token}}}","type":"OAuthException","code":190,"fbtrace_id":"Xyz789"}}""",
            HttpStatusCode.Unauthorized);
        ListLogger<WhatsAppProfileClient> log = new();

        await new WhatsAppProfileClient(meta.Factory(WhatsAppProfileClient.HttpClientName), Version(), log)
            .GetProfileAsync("15550001234", Token, CancellationToken.None);

        string refused = Assert.Single(log.Lines, line => line.StartsWith("meta.read_refused"));
        Assert.Contains("Malformed access token ***", refused);
        Assert.DoesNotContain(log.Lines, line => line.Contains(Token) || line.Contains("EAAG-"));
    }

    [Fact]
    public async Task InstagramAndFacebookReads_SendTheTokenInTheHeaderNeverTheUrl()
    {
        Meta meta = new();
        meta.Answer("/v25.0/ig-1", """{"name":"Adire House","biography":"Hand-dyed adire"}""");
        meta.Answer("/v25.0/page-1", """{"name":"Adire House","category":"Clothing"}""");

        await new InstagramProfileClient(meta.Factory(InstagramProfileClient.HttpClientName),
            Options.Create(new InstagramSettings { ApiVersion = "v25.0" }), new ListLogger<InstagramProfileClient>())
            .GetProfileAsync("ig-1", Token, CancellationToken.None);
        await new FacebookPageClient(meta.Factory(FacebookPageClient.HttpClientName),
            Options.Create(new FacebookSettings { ApiVersion = "v25.0" }), new ListLogger<FacebookPageClient>())
            .GetPageAsync("page-1", Token, CancellationToken.None);

        Assert.Equal(2, meta.Requests.Count);
        Assert.All(meta.Requests, request =>
        {
            Assert.Equal($"Bearer {Token}", request.Authorization);
            Assert.DoesNotContain(Token, request.Url);
        });
    }

    private static IOptions<WhatsAppSettings> Version() => Options.Create(new WhatsAppSettings { ApiVersion = "v25.0" });

    private sealed record Sent(string Url, string? Authorization);

    /// <summary>Graph API stand-in: answers by path and keeps what each request carried.</summary>
    private sealed class Meta : HttpMessageHandler
    {
        private readonly Dictionary<string, (string Body, HttpStatusCode Status)> _answers = [];

        public List<Sent> Requests { get; } = [];

        public void Answer(string path, string body, HttpStatusCode status = HttpStatusCode.OK) => _answers[path] = (body, status);

        public IHttpClientFactory Factory(string name) => new SingleClientFactory(name, new HttpClient(this) { BaseAddress = new Uri("https://graph.facebook.com/") });

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new Sent(request.RequestUri!.ToString(), request.Headers.Authorization?.ToString()));
            (string body, HttpStatusCode status) = _answers.TryGetValue(request.RequestUri.AbsolutePath, out var answer)
                ? answer
                : ("""{"error":{"message":"Unknown path","code":803}}""", HttpStatusCode.NotFound);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class SingleClientFactory(string name, HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string requested) =>
            requested == name ? client : throw new InvalidOperationException($"No client named {requested}.");
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }
}
