using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Parrot.Application.Payments;
using Parrot.Infrastructure.Payments;

namespace Test.Parrot.Api.Tests.Services.Payments;

// The Accelerate client against a fake Accelerate that answers the way its docs say:
// log in with the keys, keep the token, create the ticket in kobo, and turn every
// failure into a PaymentProviderException with Accelerate's own code.
public sealed class AccelerateClientTests
{
    private const string Login = """{"status":true,"message":"Login successful","data":{"token":"token-1","ttl":86400,"expires_at":"2026-11-19T14:00:00.000Z"}}""";
    private const string Ticket = """{"success":true,"message":"ok","operation_code":"0000","data":{"session_token":"sess-1","redirect_url":"https://pay.accelerate.example/s/sess-1","reference":"prt_1","description":"x"},"provider":null}""";

    private readonly FakeAccelerate _accelerate = new();
    private readonly AccelerateSession _session = new(TimeProvider.System);

    private readonly LogLines _log = new();

    private AccelerateClient Client(string? publicKey = "pk_test", string? secretKey = "sk_test") => new(
        new HttpClient(_accelerate) { BaseAddress = new Uri("https://staging.payment.example/") },
        _session,
        Options.Create(new PaymentSettings { CallbackUrl = "https://app.example/paid", Accelerate = new AccelerateSettings { PublicKey = publicKey, SecretKey = secretKey, AuthBaseUrl = "https://auth.accelerate.example/api/v1" } }),
        _log);

    [Fact]
    public async Task A_refusal_logs_what_was_sent_and_everything_that_came_back_but_no_secret()
    {
        _accelerate.Answer("api/v1/auth/api-client/token", Login);
        _accelerate.Answer("api/merchant/create-ticket",
            """{"success":false,"message":"Unknown Error","operation_code":"XXXX","data":{"session_token":"leaked-session","errors":{"callback_url":["too long"]}}}""",
            HttpStatusCode.BadRequest);

        await Assert.ThrowsAsync<PaymentProviderException>(() => Client("pk_live_key", "sk_live_secret").CreateLinkAsync(
            "prt_1", 80_050, "Ada's Bags: 2 x Quilted bag", "https://app.example/checkout/eyJ.checkout-token.sig?paid=1", default));

        string line = Assert.Single(_log.Lines, l => l.Contains("refused the payment link"));
        foreach (string part in new[] { "Status=400", "Code=XXXX", "Message=Unknown Error", "Host=staging.payment.example", "PayAmount=800.5", "Reference=prt_1",
            "Description=Ada's Bags: 2 x Quilted bag", "Callback=https://app.example/checkout/… (with a query)", "CallbackLength=58", "\"callback_url\":[\"too long\"]", "\"session_token\":\"(hidden)\"" })
        {
            Assert.Contains(part, line);
        }

        foreach (string secret in new[] { "pk_live_key", "sk_live_secret", "token-1", "checkout-token", "leaked-session" })
        {
            Assert.DoesNotContain(secret, string.Join("\n", _log.Lines));
        }
    }

    [Fact]
    public async Task A_failed_login_logs_where_it_went_and_what_came_back()
    {
        _accelerate.Answer("api/v1/auth/api-client/token", """{"status":"error","message":"Invalid credentials","token":"never-logged"}""", HttpStatusCode.Unauthorized);

        await Assert.ThrowsAsync<PaymentProviderException>(() => Client("pk_live_key", "sk_live_secret").CreateLinkAsync("prt_1", 100, "a", null, default));

        string line = Assert.Single(_log.Lines, l => l.Contains("login failed"));
        Assert.Contains("Url=https://auth.accelerate.example/api/v1/auth/api-client/token", line);
        Assert.Contains("\"message\":\"Invalid credentials\"", line);
        Assert.DoesNotContain("never-logged", line);
        Assert.DoesNotContain("sk_live_secret", line);
    }

    [Theory]
    [InlineData("https://app.example/payment/complete", "https://app.example/payment/…")]
    [InlineData("https://app.example/paid", "https://app.example/paid")]
    [InlineData("https://app.example", "https://app.example/")]
    [InlineData("not a url", "(not an absolute URL)")]
    public void A_callback_is_logged_by_its_site_and_first_step_only(string callback, string logged) =>
        Assert.Equal(logged, AccelerateClient.Shape(callback));

    [Fact]
    public void A_long_answer_is_cut_short_for_the_log() =>
        Assert.Equal(2001, AccelerateClient.ForLog(new string('x', 5000)).Length);

    /// <summary>The client's log, as the lines it would write.</summary>
    private sealed class LogLines : Microsoft.Extensions.Logging.ILogger<AccelerateClient>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }

    [Fact]
    public async Task The_pay_page_is_given_the_reference_Accelerate_leaves_off()
    {
        _accelerate.Answer("api/v1/auth/api-client/token", Login);
        _accelerate.Answer("api/merchant/create-ticket", Ticket.Replace("https://pay.accelerate.example/s/sess-1", "https://accelerate.ng/payment?"));

        PaymentLink link = await Client().CreateLinkAsync("prt_1", 800_000, "x", null, default);

        Assert.Equal("https://accelerate.ng/payment?ref=prt_1", link.Url);
    }

    [Fact]
    public async Task It_logs_in_with_the_keys_and_creates_the_ticket_in_naira()
    {
        _accelerate.Answer("api/v1/auth/api-client/token", Login);
        _accelerate.Answer("api/merchant/create-ticket", Ticket);

        PaymentLink link = await Client().CreateLinkAsync("prt_1", 800_000, "Ada's Bags: 2 x Quilted bag", null, default);

        Assert.Equal(new PaymentLink("sess-1", "https://pay.accelerate.example/s/sess-1?ref=prt_1"), link);
        // Login is on the user-management API; the ticket on the payments API.
        Assert.Equal(["auth.accelerate.example", "staging.payment.example"], _accelerate.Hosts);
        (string _, JsonElement _, string? login) = _accelerate.Calls[0];
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("pk_test:sk_test")), login);

        (string path, JsonElement ticket, string? bearer) = _accelerate.Calls[1];
        Assert.Equal("api/merchant/create-ticket", path);
        Assert.Equal("Bearer token-1", bearer);
        // 800,000 kobo is ₦8,000: Accelerate takes naira, and showed 100 times the price when sent kobo.
        Assert.Equal(8_000m, ticket.GetProperty("pay_amount").GetDecimal());
        Assert.Equal("prt_1", ticket.GetProperty("reference").GetString());
        Assert.Equal("https://app.example/paid", ticket.GetProperty("callback_url").GetString());
        Assert.Equal("Ada's Bags: 2 x Quilted bag", ticket.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Kobo_are_sent_as_naira_and_kobo()
    {
        _accelerate.Answer("api/v1/auth/api-client/token", Login);
        _accelerate.Answer("api/merchant/create-ticket", Ticket);

        await Client().CreateLinkAsync("prt_1", 80_050, "a", null, default);

        Assert.Equal(800.50m, _accelerate.Calls[1].Body.GetProperty("pay_amount").GetDecimal());
    }

    [Fact]
    public async Task The_customer_is_sent_back_to_their_checkout_when_one_is_given()
    {
        _accelerate.Answer("api/v1/auth/api-client/token", Login);
        _accelerate.Answer("api/merchant/create-ticket", Ticket);

        await Client().CreateLinkAsync("prt_1", 100, "a", "https://app.example/checkout/tok?paid=1", default);

        Assert.Equal("https://app.example/checkout/tok?paid=1", _accelerate.Calls[1].Body.GetProperty("callback_url").GetString());
    }

    [Fact]
    public async Task The_token_is_kept_between_links()
    {
        _accelerate.Answer("api/v1/auth/api-client/token", Login);
        _accelerate.Answer("api/merchant/create-ticket", Ticket);

        await Client().CreateLinkAsync("prt_1", 100, "a", null, default);
        await Client().CreateLinkAsync("prt_2", 100, "b", null, default);

        Assert.Equal(1, _accelerate.Calls.Count(c => c.Path == "api/v1/auth/api-client/token"));
    }

    [Fact]
    public async Task A_refused_token_is_replaced_once()
    {
        _accelerate.Answer("api/v1/auth/api-client/token", Login);
        _accelerate.Answer("api/merchant/create-ticket", """{"success":false,"message":"Unauthorized"}""", HttpStatusCode.Unauthorized);
        _accelerate.Then("api/merchant/create-ticket", Ticket);

        PaymentLink link = await Client().CreateLinkAsync("prt_1", 100, "a", null, default);

        Assert.Equal("sess-1", link.SessionId);
        Assert.Equal(["api/v1/auth/api-client/token", "api/merchant/create-ticket", "api/v1/auth/api-client/token", "api/merchant/create-ticket"], _accelerate.Calls.Select(c => c.Path));
    }

    [Fact]
    public async Task A_duplicate_reference_comes_back_with_accelerates_code()
    {
        _accelerate.Answer("api/v1/auth/api-client/token", Login);
        _accelerate.Answer("api/merchant/create-ticket", """{"success":false,"message":"Duplicate reference","operation_code":"00H1","data":null}""", HttpStatusCode.BadRequest);

        var failed = await Assert.ThrowsAsync<PaymentProviderException>(() => Client().CreateLinkAsync("prt_1", 100, "a", null, default));

        Assert.Equal("00H1", failed.Code);
        Assert.Equal("Duplicate reference", failed.Message);
    }

    [Fact]
    public async Task Bad_keys_unreachable_or_rate_limited_all_fail_as_provider_errors()
    {
        _accelerate.Answer("api/v1/auth/api-client/token", """{"status":"error","message":"Invalid credentials"}""", HttpStatusCode.Unauthorized);
        await Assert.ThrowsAsync<PaymentProviderException>(() => Client().CreateLinkAsync("prt_1", 100, "a", null, default));

        _accelerate.Answer("api/v1/auth/api-client/token", Login);
        _accelerate.Answer("api/merchant/create-ticket", """{"retry_after":30}""", HttpStatusCode.TooManyRequests);
        var limited = await Assert.ThrowsAsync<PaymentProviderException>(() => Client().CreateLinkAsync("prt_1", 100, "a", null, default));
        Assert.Contains("rate limit", limited.Message);

        _accelerate.Unreachable = true;
        _session.Forget();
        await Assert.ThrowsAsync<PaymentProviderException>(() => Client().CreateLinkAsync("prt_1", 100, "a", null, default));
    }

    [Fact]
    public async Task A_success_without_a_link_is_not_a_link()
    {
        _accelerate.Answer("api/v1/auth/api-client/token", Login);
        _accelerate.Answer("api/merchant/create-ticket", """{"success":true,"operation_code":"0000","data":{"session_token":"sess-1"}}""");

        await Assert.ThrowsAsync<PaymentProviderException>(() => Client().CreateLinkAsync("prt_1", 100, "a", null, default));
    }

    [Fact]
    public void Without_keys_it_is_not_configured()
    {
        Assert.True(Client().IsConfigured);
        Assert.False(Client(publicKey: null).IsConfigured);
        Assert.False(Client(secretKey: " ").IsConfigured);
    }

    private sealed class FakeAccelerate : HttpMessageHandler
    {
        private readonly Dictionary<string, Queue<(string Body, HttpStatusCode Status)>> _answers = [];

        public List<(string Path, JsonElement Body, string? Bearer)> Calls { get; } = [];

        public List<string> Hosts { get; } = [];

        public bool Unreachable { get; set; }

        public void Answer(string path, string body, HttpStatusCode status = HttpStatusCode.OK) =>
            _answers[path] = new Queue<(string, HttpStatusCode)>([(body, status)]);

        /// <summary>The answer after the current one.</summary>
        public void Then(string path, string body, HttpStatusCode status = HttpStatusCode.OK) => _answers[path].Enqueue((body, status));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Unreachable) throw new HttpRequestException("Connection refused");
            string path = request.RequestUri!.AbsolutePath.TrimStart('/');
            JsonElement body = request.Content is null ? default : JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
            Calls.Add((path, body, request.Headers.Authorization?.ToString()));
            Hosts.Add(request.RequestUri.Host);

            Queue<(string Body, HttpStatusCode Status)> queue = _answers[path];
            (string text, HttpStatusCode status) = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
            return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }
}
