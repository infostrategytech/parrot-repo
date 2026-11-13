using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Parrot.Api.Mcp;
using Parrot.Api.Security;
using Parrot.Application.Agents;
using Parrot.Application.Auth;

namespace Test.Parrot.Api.Tests.Mcp;

/// <summary>
/// The agent's MCP server end to end: a real MCP client connects over HTTP with a service
/// token, lists the tools and calls them. The catalog behind them is a fake that records
/// what it was asked, so the tests can check the workspace comes from the token.
/// </summary>
public sealed class AgentToolsMcpTests : IAsyncLifetime
{
    private const string Secret = "test-secret-that-is-long-enough-for-hmac-sha256-0123456789";
    private const string Workspace = "8f14e45f-ceea-467f-a0e6-2b9d1c7f3a21";

    private readonly FakeCatalog _catalog = new ();
    private WebApplication _app = null!;
    private Uri _endpoint = null!;

    public async Task InitializeAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = false,
                ValidateAudience = false,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)),
            };
        });
        builder.Services.AddServiceScopePolicy();
        builder.Services.AddSingleton<ICatalogToolsClient>(_catalog);
        builder.Services.AddAgentTools();

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapAgentTools();
        await _app.StartAsync();

        string address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _endpoint = new Uri(address + McpEndpoint.Path);
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task The_agent_sees_the_product_tools_with_their_inputs()
    {
        await using McpClient client = await ConnectAsync(AgentToken());

        IList<McpClientTool> tools = await client.ListToolsAsync();

        McpClientTool prices = Assert.Single(tools, t => t.Name == "get_prices");
        Assert.Contains("never quote them from memory", prices.Description);
        JsonElement properties = prices.JsonSchema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("ids", out _));
        Assert.True(properties.TryGetProperty("min_price", out _));
        Assert.True(properties.TryGetProperty("max_price", out _));
        Assert.Equal("array", properties.GetProperty("ids").GetProperty("type").GetString());
        Assert.Contains(tools, t => t.Name == "search_products");
    }

    [Fact]
    public async Task Prices_are_asked_for_the_workspace_in_the_token_and_passed_back_as_the_catalog_sent_them()
    {
        Guid bag = Guid.NewGuid();
        _catalog.Answer = """{"tool":"get_prices","result":{"items":[{"product_id":"x","price":4000}],"not_on_sale":[]}}""";
        await using McpClient client = await ConnectAsync(AgentToken());

        CallToolResult result = await client.CallToolAsync("get_prices", new Dictionary<string, object?>
        {
            ["ids"] = new[] { bag },
            ["min_price"] = 2000,
            ["max_price"] = 10000,
            ["tenant_id"] = "someone-else",
        });

        Assert.NotEqual(true, result.IsError);
        (string workspace, string tool, JsonElement arguments) = Assert.Single(_catalog.Calls);
        Assert.Equal(Workspace, workspace);
        Assert.Equal("get_prices", tool);
        Assert.Equal(bag, arguments.GetProperty("ids")[0].GetGuid());
        Assert.Equal(2000m, arguments.GetProperty("min_price").GetDecimal());
        Assert.Equal(10000m, arguments.GetProperty("max_price").GetDecimal());
        Assert.Contains("4000", Assert.IsType<TextContentBlock>(result.Content[0]).Text);
    }

    [Fact]
    public async Task A_refusal_reaches_the_agent_in_words_and_an_outage_tells_it_not_to_guess()
    {
        await using McpClient client = await ConnectAsync(AgentToken());

        _catalog.Fail = new CatalogToolException("ids may hold at most 20 products.", refused: true);
        CallToolResult refused = await client.CallToolAsync("get_prices", new Dictionary<string, object?> { ["ids"] = new[] { Guid.NewGuid() } });
        Assert.True(refused.IsError);
        Assert.Contains("ids may hold at most 20 products.", Text(refused));

        _catalog.Fail = new CatalogToolException("The catalog can't be reached right now.", refused: false);
        CallToolResult down = await client.CallToolAsync("search_products", new Dictionary<string, object?> { ["query"] = "bag" });
        Assert.True(down.IsError);
        Assert.Contains("Don't quote prices", Text(down));

        _catalog.Fail = null;
        CallToolResult tooMany = await client.CallToolAsync("get_prices", new Dictionary<string, object?> { ["ids"] = Enumerable.Range(0, 21).Select(_ => Guid.NewGuid()).ToArray() });
        Assert.True(tooMany.IsError);
        Assert.Empty(_catalog.Calls);
    }

    [Fact]
    public async Task Search_trims_the_words_and_keeps_the_limit_in_range()
    {
        _catalog.Answer = """{"tool":"search_catalog","result":{"items":[],"count":0}}""";
        await using McpClient client = await ConnectAsync(AgentToken());

        await client.CallToolAsync("search_products", new Dictionary<string, object?> { ["query"] = "  bag ", ["limit"] = 50 });

        (_, string tool, JsonElement arguments) = Assert.Single(_catalog.Calls);
        Assert.Equal("search_catalog", tool);
        Assert.Equal("bag", arguments.GetProperty("query").GetString());
        Assert.Equal(10, arguments.GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task A_person_s_session_token_or_no_token_cannot_open_the_tools()
    {
        string session = Token(new Claim("sub", "user-1"), new Claim("workspace_id", Workspace), new Claim("tenant_id", Workspace));

        HttpRequestException forbidden = await Assert.ThrowsAsync<HttpRequestException>(() => ConnectAsync(session));
        HttpRequestException anonymous = await Assert.ThrowsAsync<HttpRequestException>(() => ConnectAsync(null));
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Empty(_catalog.Calls);
    }

    private static string AgentToken() => Token(
        new Claim("sub", Workspace),
        new Claim(ServiceScopes.TenantClaim, Workspace),
        new Claim(ServiceScopes.ScopeClaim, ServiceScopes.AgentService),
        new Claim(ServiceScopes.ServiceClaim, "omni"));

    private static string Token(params Claim[] claims)
    {
        SigningCredentials credentials = new (new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256);
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(claims: claims, expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: credentials));
    }

    private async Task<McpClient> ConnectAsync(string? token)
    {
        HttpClientTransportOptions options = new () { Endpoint = _endpoint };
        if (token is not null)
        {
            options.AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" };
        }

        return await McpClient.CreateAsync(new HttpClientTransport(options));
    }

    private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    private sealed class FakeCatalog : ICatalogToolsClient
    {
        public List<(string Workspace, string Tool, JsonElement Arguments)> Calls { get; } = [];

        public string Answer { get; set; } = "{}";

        public CatalogToolException? Fail { get; set; }

        public Task<JsonElement> CallAsync(string workspaceId, string tool, object arguments, CancellationToken ct)
        {
            if (Fail is not null)
            {
                throw Fail;
            }

            JsonElement sent = JsonSerializer.SerializeToElement(arguments, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
            Calls.Add((workspaceId, tool, sent));
            return Task.FromResult(JsonDocument.Parse(Answer).RootElement.Clone());
        }
    }
}
