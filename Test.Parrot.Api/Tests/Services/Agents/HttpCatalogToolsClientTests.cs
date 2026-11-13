using System.Net;
using System.Text;
using System.Text.Json;
using Moq;
using Parrot.Application.Agents;
using Parrot.Application.Auth;
using Parrot.Infrastructure.Agents;

namespace Test.Parrot.Api.Tests.Services.Agents;

public class HttpCatalogToolsClientTests
{
    private const string Workspace = "workspace-1";

    [Fact]
    public async Task It_calls_the_tool_with_an_ai_tools_token_for_the_workspace_in_snake_case()
    {
        FakeHandler handler = new (HttpStatusCode.OK, """{"tool":"get_prices","result":{"items":[]}}""");
        HttpCatalogToolsClient client = Create(handler, out Mock<IServiceTokenFactory> tokens);

        JsonElement result = await client.CallAsync(Workspace, "get_prices", new { Ids = new[] { Guid.Empty }, MaxPrice = 5000m }, CancellationToken.None);

        Assert.Equal("get_prices", result.GetProperty("tool").GetString());
        Assert.Equal("http://catalog/api/catalog/ai/tools/get_prices", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer ai-tools-token", handler.Request.Headers.Authorization!.ToString());
        Assert.Contains("\"max_price\":5000", handler.Body);
        tokens.Verify(t => t.MintForCatalogAiTools(Workspace), Times.Once);
        tokens.Verify(t => t.MintForCatalog(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task A_refusal_carries_the_catalogs_own_words()
    {
        HttpCatalogToolsClient client = Create(new FakeHandler(HttpStatusCode.BadRequest, """{"error_code":"VALIDATION","message":"ids needs at least one product id."}"""), out _);

        CatalogToolException error = await Assert.ThrowsAsync<CatalogToolException>(() => client.CallAsync(Workspace, "get_prices", new { }, CancellationToken.None));

        Assert.True(error.Refused);
        Assert.Equal("ids needs at least one product id.", error.Message);
    }

    [Fact]
    public async Task A_server_error_or_a_missing_catalog_is_an_outage_not_a_refusal()
    {
        HttpCatalogToolsClient broken = Create(new FakeHandler(HttpStatusCode.InternalServerError, "oops"), out _);
        Assert.False((await Assert.ThrowsAsync<CatalogToolException>(() => broken.CallAsync(Workspace, "get_prices", new { }, CancellationToken.None))).Refused);

        HttpCatalogToolsClient unset = new (new HttpClient(new FakeHandler(HttpStatusCode.OK, "{}")), new Mock<IServiceTokenFactory>().Object);
        Assert.False((await Assert.ThrowsAsync<CatalogToolException>(() => unset.CallAsync(Workspace, "get_prices", new { }, CancellationToken.None))).Refused);
    }

    private static HttpCatalogToolsClient Create(FakeHandler handler, out Mock<IServiceTokenFactory> tokens)
    {
        tokens = new Mock<IServiceTokenFactory>();
        tokens.Setup(t => t.MintForCatalogAiTools(Workspace)).Returns("ai-tools-token");
        return new HttpCatalogToolsClient(new HttpClient(handler) { BaseAddress = new Uri("http://catalog/") }, tokens.Object);
    }

    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string Body { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
