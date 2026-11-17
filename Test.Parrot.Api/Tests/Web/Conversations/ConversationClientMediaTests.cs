using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Parrot.Web.Features.Auth.Models;
using Parrot.Web.Features.Auth.State;
using Parrot.Web.Services;

namespace Test.Parrot.Api.Tests.Web.Conversations;

// A picture in the inbox is loaded by the browser, so its address must be one the browser can reach:
// the API's public address when the web server calls the API by an internal name.
public sealed class ConversationClientMediaTests
{
    private const string Page = """
        {"messages":[{"id":"m1","text":"Product: Bag","outgoing":true,"status":"accepted","receivedAt":"2026-11-17T15:53:00Z","kind":"ai",
          "attachments":[{"kind":"image","contentType":"image/jpeg","fileName":"bag.jpg","sizeBytes":10,"url":"api/media/signed-key"}]}],
         "nextBefore":null}
        """;

    private static ConversationClient Client(IConfiguration? configuration)
    {
        AuthState auth = new();
        auth.SetUser(new AuthResponse { Token = "t", UserId = "u", Email = "a@b.c", ExpiresAt = DateTime.UtcNow.AddHours(1) });
        HttpClient http = new(new Answer()) { BaseAddress = new Uri("http://parrot-api:8080/") };
        return new ConversationClient(http, auth, configuration);
    }

    [Fact]
    public async Task A_picture_uses_the_apis_public_address_when_there_is_one()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ApiSettings:PublicBaseUrl"] = "http://localhost:5222/" })
            .Build();

        var page = await Client(configuration).ReadAsync(Guid.NewGuid(), "2348011112222", null, default);

        Assert.Equal("http://localhost:5222/api/media/signed-key", Assert.Single(page.Messages[0].Attachments!).Url);
    }

    [Fact]
    public async Task Without_a_public_address_the_api_address_is_used_as_before()
    {
        var page = await Client(null).ReadAsync(Guid.NewGuid(), "2348011112222", null, default);

        Assert.Equal("http://parrot-api:8080/api/media/signed-key", Assert.Single(page.Messages[0].Attachments!).Url);
    }

    private sealed class Answer : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Page, Encoding.UTF8, "application/json") });
    }
}
