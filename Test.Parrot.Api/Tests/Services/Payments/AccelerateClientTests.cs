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
    private const string Login = """{"status":"success","data":{"token":"token-1","expires_in":"86400"}}""";
    private const string Ticket = """{"success":true,"message":"ok","operation_code":"0000","data":{"session_token":"sess-1","redirect_url":"https://pay.accelerate.example/s/sess-1","reference":"prt_1","description":"x"},"provider":null}""";

    private readonly FakeAccelerate _accelerate = new();
    private readonly AccelerateSession _session = new(TimeProvider.System);

    private AccelerateClient Client(string? publicKey = "pk_test", string? secretKey = "sk_test") => new(
        new HttpClient(_accelerate) { BaseAddress = new Uri("https://staging.payment.example/") },
        _session,
        Options.Create(new PaymentSettings { CallbackUrl = "https://app.example/paid", Accelerate = new AccelerateSettings { PublicKey = publicKey, SecretKey = secretKey } }),
        NullLogger<AccelerateClient>.Instance);

    [Fact]
    public async Task It_logs_in_with_the_keys_and_creates_the_ticket_in_kobo()
    {
        _accelerate.Answer("auth/login", Login);
        _accelerate.Answer("api/merchant/create-ticket", Ticket);

        PaymentLink link = await Client().CreateLinkAsync("prt_1", 800_000, "Ada's Bags: 2 x Quilted bag", default);

        Assert.Equal(new PaymentLink("sess-1", "https://pay.accelerate.example/s/sess-1"), link);
        (string _, JsonElement login, string? _) = _accelerate.Calls[0];
        Assert.Equal("pk_test", login.GetProperty("username").GetString());
        Assert.Equal("sk_test", login.GetProperty("password").GetString());

        (string path, JsonElement ticket, string? bearer) = _accelerate.Calls[1];
        Assert.Equal("api/merchant/create-ticket", path);
        Assert.Equal("Bearer token-1", bearer);
        Assert.Equal(800_000, ticket.GetProperty("pay_amount").GetInt64());
        Assert.Equal("prt_1", ticket.GetProperty("reference").GetString());
        Assert.Equal("https://app.example/paid", ticket.GetProperty("callback_url").GetString());
        Assert.Equal("Ada's Bags: 2 x Quilted bag", ticket.GetProperty("description").GetString());
    }

    [Fact]
    public async Task The_token_is_kept_between_links()
    {
        _accelerate.Answer("auth/login", Login);
        _accelerate.Answer("api/merchant/create-ticket", Ticket);

        await Client().CreateLinkAsync("prt_1", 100, "a", default);
        await Client().CreateLinkAsync("prt_2", 100, "b", default);

        Assert.Equal(1, _accelerate.Calls.Count(c => c.Path == "auth/login"));
    }

    [Fact]
    public async Task A_refused_token_is_replaced_once()
    {
        _accelerate.Answer("auth/login", Login);
        _accelerate.Answer("api/merchant/create-ticket", """{"success":false,"message":"Unauthorized"}""", HttpStatusCode.Unauthorized);
        _accelerate.Then("api/merchant/create-ticket", Ticket);

        PaymentLink link = await Client().CreateLinkAsync("prt_1", 100, "a", default);

        Assert.Equal("sess-1", link.SessionId);
        Assert.Equal(["auth/login", "api/merchant/create-ticket", "auth/login", "api/merchant/create-ticket"], _accelerate.Calls.Select(c => c.Path));
    }

    [Fact]
    public async Task A_duplicate_reference_comes_back_with_accelerates_code()
    {
        _accelerate.Answer("auth/login", Login);
        _accelerate.Answer("api/merchant/create-ticket", """{"success":false,"message":"Duplicate reference","operation_code":"00H1","data":null}""", HttpStatusCode.BadRequest);

        var failed = await Assert.ThrowsAsync<PaymentProviderException>(() => Client().CreateLinkAsync("prt_1", 100, "a", default));

        Assert.Equal("00H1", failed.Code);
        Assert.Equal("Duplicate reference", failed.Message);
    }

    [Fact]
    public async Task Bad_keys_unreachable_or_rate_limited_all_fail_as_provider_errors()
    {
        _accelerate.Answer("auth/login", """{"status":"error","message":"Invalid credentials"}""", HttpStatusCode.Unauthorized);
        await Assert.ThrowsAsync<PaymentProviderException>(() => Client().CreateLinkAsync("prt_1", 100, "a", default));

        _accelerate.Answer("auth/login", Login);
        _accelerate.Answer("api/merchant/create-ticket", """{"retry_after":30}""", HttpStatusCode.TooManyRequests);
        var limited = await Assert.ThrowsAsync<PaymentProviderException>(() => Client().CreateLinkAsync("prt_1", 100, "a", default));
        Assert.Contains("rate limit", limited.Message);

        _accelerate.Unreachable = true;
        _session.Forget();
        await Assert.ThrowsAsync<PaymentProviderException>(() => Client().CreateLinkAsync("prt_1", 100, "a", default));
    }

    [Fact]
    public async Task A_success_without_a_link_is_not_a_link()
    {
        _accelerate.Answer("auth/login", Login);
        _accelerate.Answer("api/merchant/create-ticket", """{"success":true,"operation_code":"0000","data":{"session_token":"sess-1"}}""");

        await Assert.ThrowsAsync<PaymentProviderException>(() => Client().CreateLinkAsync("prt_1", 100, "a", default));
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

        public bool Unreachable { get; set; }

        public void Answer(string path, string body, HttpStatusCode status = HttpStatusCode.OK) =>
            _answers[path] = new Queue<(string, HttpStatusCode)>([(body, status)]);

        /// <summary>The answer after the current one.</summary>
        public void Then(string path, string body, HttpStatusCode status = HttpStatusCode.OK) => _answers[path].Enqueue((body, status));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Unreachable) throw new HttpRequestException("Connection refused");
            string path = request.RequestUri!.AbsolutePath.TrimStart('/');
            JsonElement body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
            Calls.Add((path, body, request.Headers.Authorization?.ToString()));

            Queue<(string Body, HttpStatusCode Status)> queue = _answers[path];
            (string text, HttpStatusCode status) = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
            return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }
}
