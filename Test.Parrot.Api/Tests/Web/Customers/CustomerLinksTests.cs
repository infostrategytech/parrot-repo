using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Parrot.Web.Features.Auth.Models;
using Parrot.Web.Features.Auth.State;
using Parrot.Web.Services;

namespace Test.Parrot.Api.Tests.Web.Customers;

// Controlled contract responses only: no dependency on the local demo customer dataset.
public class CustomerLinksTests
{
    private static readonly Guid Source = Guid.Parse("0971bdc3-37a2-467a-aefd-364167474594");
    private static readonly Guid Target = Guid.Parse("a599ec18-bf68-4bd0-af25-051952c43a25");
    private static readonly Guid Link = Guid.Parse("52ea743e-e909-42d3-bbd4-7061c032cb6c");
    private static LinkedCustomerProfile Profile => new(new CustomerDetailDto(Target, "Example customer", null, "Customer", [], [], null, null,
        DateTime.UnixEpoch, null, null, null), []);

    [Fact]
    public async Task LinkingSendsTheSelectedTargetWithoutInventingANameMatch()
    {
        var (client, handler) = Client();
        var result = await client.LinkAsync(Source, Target, CancellationToken.None);
        Assert.Equal($"/api/customers/{Source}/links", handler.Path);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal(Target, JsonSerializer.Deserialize<LinkCustomerRequest>(handler.Body!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.CustomerId);
        Assert.Equal(Target, result.Customer.Id);
        Assert.Equal("Bearer", handler.Scheme);
        Assert.Equal("controlled-test-token", handler.Token);
    }

    [Fact]
    public async Task SeparatingAddressesTheSpecificLinkNotTheCustomerOrMessages()
    {
        var (client, handler) = Client();
        await client.UnlinkAsync(Target, Link, CancellationToken.None);
        Assert.Equal($"/api/customers/{Target}/links/{Link}", handler.Path);
        Assert.Equal(HttpMethod.Delete, handler.Method);
        Assert.Null(handler.Body);
    }

    [Fact]
    public async Task InboxLookupUsesConnectionAndExactEscapedSender()
    {
        var (client, handler) = Client(new CustomerLinkLookup(Profile));
        var result = await client.FindAsync(Source, "ig:123&other=456", CancellationToken.None);
        Assert.Equal($"/api/customers/resolve?connectionId={Source}&sender=ig%3A123%26other%3D456", handler.Path);
        Assert.Equal(Target, result!.Customer.Id);
    }

    [Fact]
    public async Task AnUnresolvedConversationDoesNotBecomeAnAutomaticLink()
    {
        var (client, _) = Client(new CustomerLinkLookup(null));
        Assert.Null(await client.FindAsync(Source, "unknown", CancellationToken.None));
    }

    [Fact]
    public async Task StaleOrConflictingLinksShowTheServerError()
    {
        var (client, handler) = Client(new { message = "These profiles have changed." });
        handler.Status = HttpStatusCode.Conflict;
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.LinkAsync(Source, Target, CancellationToken.None));
        Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
        Assert.Equal("These profiles have changed.", error.Message);
    }

    [Fact]
    public async Task SignedOutUsersDoNotSendLinkRequests()
    {
        var handler = new ControlledHandler(Profile);
        var client = new CustomerLinkClient(new CustomerClient(new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") }, new AuthState()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.LinkAsync(Source, Target, CancellationToken.None));
        Assert.Null(handler.Path);
    }

    [Fact]
    public async Task UnsupportedBackendDoesNotOfferLinkingOrSimulateSuccess()
    {
        ICustomerLinks service = new UnavailableCustomerLinks();
        Assert.False(service.Available);
        await Assert.ThrowsAsync<NotSupportedException>(() => service.LinkAsync(Source, Target, CancellationToken.None));
    }

    private static (CustomerLinkClient Client, ControlledHandler Handler) Client(object? reply = null)
    {
        var auth = new AuthState();
        auth.SetUser(new AuthResponse { Token = "controlled-test-token", UserId = "test-owner", WorkspaceId = "test-workspace", ExpiresAt = DateTime.UtcNow.AddHours(1) });
        var handler = new ControlledHandler(reply ?? Profile);
        return (new CustomerLinkClient(new CustomerClient(new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") }, auth)), handler);
    }

    private sealed class ControlledHandler(object reply) : HttpMessageHandler
    {
        public string? Path, Body, Scheme, Token;
        public HttpMethod? Method;
        public HttpStatusCode Status = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Path = request.RequestUri!.PathAndQuery;
            Method = request.Method;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Scheme = request.Headers.Authorization?.Scheme;
            Token = request.Headers.Authorization?.Parameter;
            return new HttpResponseMessage(Status) { Content = JsonContent.Create(reply) };
        }
    }
}
