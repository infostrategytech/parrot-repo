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
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Moq;
using Parrot.Api.Mcp;
using Parrot.Api.Security;
using Parrot.Application.Agents;
using Parrot.Application.Auth;
using Parrot.Application.FeatureFlags;
using Parrot.Application.Payments;
using Parrot.Application.Teams;
using Parrot.Application.Workspaces;
using Parrot.Domain.Entities;
using Parrot.Domain.Enums;
using Test.Parrot.Api.Tests.Services.Payments;
using Test.Parrot.Api.Tests.Services.Teams;
using Test.Parrot.Api.Tests.Services.Workspaces;

namespace Test.Parrot.Api.Tests.Mcp;

/// <summary>
/// create_checkout end to end: a real MCP client calls it over HTTP with the agent's service
/// token, and gets a checkout link priced from the catalog for the token's workspace.
/// </summary>
public sealed class PaymentToolsMcpTests : IAsyncLifetime
{
    private const string Secret = "test-secret-that-is-long-enough-for-hmac-sha256-0123456789";
    private const string Workspace = "8f14e45f-ceea-467f-a0e6-2b9d1c7f3a21";

    private readonly FakePriceCatalog _catalog = new();
    private readonly FakePaymentProvider _provider = new();
    private readonly FakePaymentOrderRepository _orders = new();
    private readonly FakeConnectionDirectory _connections = new();
    private readonly FakeWorkspaceRepository _workspaces = new();
    private readonly Guid _agent = Guid.NewGuid();
    private Guid _connection;
    private WebApplication _app = null!;
    private Uri _endpoint = null!;

    public async Task InitializeAsync()
    {
        _workspaces.Seed(Workspace, "owner", name: "Ada's Bags");
        _connection = _connections.Add(Workspace, MessagingPlatform.WhatsApp);
        Mock<IAgentRepository> agents = new();
        agents.Setup(a => a.GetByIdAsync(_agent)).ReturnsAsync(new Agent { Id = _agent, WorkspaceId = Workspace, Name = "Ngozi", Description = "", MascotUrl = "" });

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
        builder.Services.AddSingleton<IPaymentProvider>(_provider);
        builder.Services.AddSingleton<IPaymentOrderRepository>(_orders);
        builder.Services.AddSingleton<IWorkspaceConnectionDirectory>(_connections);
        builder.Services.AddSingleton<IWorkspaceRepository>(_workspaces);
        builder.Services.AddSingleton(agents.Object);
        builder.Services.AddSingleton<IFeatureFlagService>(new FakeFlags());
        PaymentSettings settings = new() { CallbackUrl = "https://app.example/paid" };
        builder.Services.AddSingleton(Options.Create(settings));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IWorkspacePaymentSettingsRepository>(new FakeWorkspacePaymentSettingsRepository());
        builder.Services.AddScoped<WorkspacePaymentSettingsService>();
        builder.Services.AddSingleton(CheckoutServiceTests.Links(settings, "a-secret-long-enough-for-hmac-sha256-signing!!"));
        builder.Services.AddScoped<CheckoutService>();
        builder.Services.AddSingleton<ICatalogStockClient>(new FakeStockClient());
        builder.Services.AddSingleton<IPaymentNotifier>(new FakeNotifier());
        builder.Services.AddScoped<PaymentConfirmationService>();
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
    public async Task The_agent_sees_create_checkout_with_items_and_no_price_input()
    {
        await using McpClient client = await ConnectAsync();

        McpClientTool tool = Assert.Single(await client.ListToolsAsync(), t => t.Name == "create_checkout");

        Assert.Contains("never prices", tool.Description);
        JsonElement properties = tool.JsonSchema.GetProperty("properties");
        Assert.Equal(
            ["agent_id", "connection_id", "customer_email", "customer_name", "customer_phone", "idempotency_key", "items", "note", "sender"],
            properties.EnumerateObject().Select(p => p.Name).Order());
        JsonElement item = properties.GetProperty("items").GetProperty("items").GetProperty("properties");
        Assert.Equal(["product_id", "quantity", "variant_id"], item.EnumerateObject().Select(p => p.Name).Order());
        Assert.DoesNotContain("price", tool.JsonSchema.GetRawText().Replace("prices", "").Replace("priced", ""));
    }

    [Fact]
    public async Task The_agent_gets_a_checkout_priced_from_the_catalog_for_its_workspace()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m, left: 5);
        await using McpClient client = await ConnectAsync();

        CallToolResult result = await client.CallToolAsync("create_checkout", Arguments(new[] { new { product_id = bag, quantity = 2 } }));

        Assert.NotEqual(true, result.IsError);
        using JsonDocument answer = JsonDocument.Parse(Text(result));
        JsonElement link = answer.RootElement;
        Assert.Equal("checkout_ready", link.GetProperty("status").GetString());
        Assert.Equal(8000m, link.GetProperty("total").GetDecimal());
        Assert.Equal("NGN", link.GetProperty("currency").GetString());
        Assert.StartsWith("https://app.example/checkout/", link.GetProperty("checkout_url").GetString());
        Assert.Equal("Quilted bag", link.GetProperty("lines")[0].GetProperty("name").GetString());
        Assert.Contains("Don't tell the customer the order is paid", link.GetProperty("for_the_agent").GetString());
        Assert.Equal((Workspace, "get_prices"), Assert.Single(_catalog.Calls));
        Assert.Empty(_provider.Calls);
        Assert.Equal(800_000, Assert.Single(_orders.Orders).TotalMinor);
    }

    [Fact]
    public async Task The_agent_can_check_whether_its_checkout_was_paid()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m, left: 5);
        await using McpClient client = await ConnectAsync();
        CallToolResult made = await client.CallToolAsync("create_checkout", Arguments(new[] { new { product_id = bag, quantity = 1 } }));
        string orderId = JsonDocument.Parse(Text(made)).RootElement.GetProperty("order_id").GetString()!;

        CallToolResult status = await client.CallToolAsync("get_payment_status", new Dictionary<string, object?> { ["order_id"] = orderId });

        Assert.NotEqual(true, status.IsError);
        JsonElement answer = JsonDocument.Parse(Text(status)).RootElement;
        Assert.Equal("awaiting_payment", answer.GetProperty("status").GetString());
        Assert.StartsWith("https://app.example/checkout/", answer.GetProperty("checkout_url").GetString());
        Assert.Contains("Don't say it's paid", answer.GetProperty("for_the_agent").GetString());

        CallToolResult unknown = await client.CallToolAsync("get_payment_status", new Dictionary<string, object?> { ["order_id"] = Guid.NewGuid() });
        Assert.True(unknown.IsError);

        // The prt_ reference finds the same order; anything else gets words, not a bare failure.
        string reference = _orders.Orders.Single(o => o.Id == Guid.Parse(orderId)).Reference;
        CallToolResult byReference = await client.CallToolAsync("get_payment_status", new Dictionary<string, object?> { ["order_id"] = reference });
        Assert.Equal(orderId, JsonDocument.Parse(Text(byReference)).RootElement.GetProperty("order_id").GetString());

        CallToolResult garbled = await client.CallToolAsync("get_payment_status", new Dictionary<string, object?> { ["order_id"] = "{\"order_id\": \"x\"}" });
        Assert.True(garbled.IsError);
        Assert.Contains("Pass the order_id create_checkout returned", Text(garbled));
    }

    [Fact]
    public async Task A_refusal_reaches_the_agent_in_words_it_can_act_on()
    {
        Guid purse = _catalog.Product("Beaded purse", 3000m, left: 0, available: false);
        await using McpClient client = await ConnectAsync();

        CallToolResult result = await client.CallToolAsync("create_checkout", Arguments(new[] { new { product_id = purse, quantity = 1 } }));

        Assert.True(result.IsError);
        Assert.Contains("Beaded purse is sold out.", Text(result));
        Assert.Empty(_provider.Calls);
    }

    private Dictionary<string, object?> Arguments(object items) => new()
    {
        ["agent_id"] = _agent,
        ["connection_id"] = _connection,
        ["sender"] = "2348012345678",
        ["items"] = items,
        ["idempotency_key"] = Guid.NewGuid().ToString(),
    };

    private async Task<McpClient> ConnectAsync()
    {
        SigningCredentials credentials = new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256);
        string token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            claims: [new Claim("sub", Workspace), new Claim(ServiceScopes.TenantClaim, Workspace), new Claim(ServiceScopes.ScopeClaim, ServiceScopes.AgentService), new Claim(ServiceScopes.ServiceClaim, "omni")],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials));
        HttpClientTransportOptions options = new() { Endpoint = _endpoint, AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" } };
        return await McpClient.CreateAsync(new HttpClientTransport(options));
    }

    private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
}
