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
/// create_cart end to end: a real MCP client calls it over HTTP with the agent's service
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
    // As Omni sends it: Omni's own agent id, not a UUID.
    private const string OmniAgent = "agt_01M6QZQW8DFM3PARMN5SXEZSE7";
    private Guid _connection;
    private WebApplication _app = null!;
    private Uri _endpoint = null!;

    public async Task InitializeAsync()
    {
        _workspaces.Seed(Workspace, "owner", name: "Ada's Bags");
        _connection = _connections.Add(Workspace, MessagingPlatform.WhatsApp);

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
    public async Task The_agent_sees_create_cart_with_items_and_no_price_input()
    {
        await using McpClient client = await ConnectAsync();

        McpClientTool tool = Assert.Single(await client.ListToolsAsync(), t => t.Name == "create_cart");

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

        CallToolResult result = await client.CallToolAsync("create_cart", Arguments(new[] { new { product_id = bag, quantity = 2 } }));

        Assert.NotEqual(true, result.IsError);
        using JsonDocument answer = JsonDocument.Parse(Text(result));
        JsonElement link = answer.RootElement;
        Assert.Equal("cart_ready", link.GetProperty("status").GetString());
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
        CallToolResult made = await client.CallToolAsync("create_cart", Arguments(new[] { new { product_id = bag, quantity = 1 } }));
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
        Assert.Contains("Pass the order_id create_cart returned", Text(garbled));
    }

    [Fact]
    public async Task The_agent_changes_the_cart_and_reads_it_back_by_the_conversation()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m, left: 5);
        await using McpClient client = await ConnectAsync();
        CallToolResult made = await client.CallToolAsync("create_cart", Arguments(new[] { new { product_id = bag, quantity = 1 } }));
        string orderId = JsonDocument.Parse(Text(made)).RootElement.GetProperty("order_id").GetString()!;

        CallToolResult changed = await client.CallToolAsync("create_cart", Arguments(new[] { new { product_id = bag, quantity = 3 } }));
        JsonElement change = JsonDocument.Parse(Text(changed)).RootElement;
        Assert.Equal(("cart_updated", orderId), (change.GetProperty("status").GetString(), change.GetProperty("order_id").GetString()));

        CallToolResult read = await client.CallToolAsync("get_cart", new Dictionary<string, object?> { ["connection_id"] = _connection, ["sender"] = "2348012345678" });

        Assert.NotEqual(true, read.IsError);
        JsonElement cart = JsonDocument.Parse(Text(read)).RootElement;
        Assert.Equal((orderId, "open", true, 12000m), (cart.GetProperty("order_id").GetString(), cart.GetProperty("state").GetString(),
            cart.GetProperty("can_pay").GetBoolean(), cart.GetProperty("total").GetDecimal()));
        JsonElement item = cart.GetProperty("items")[0];
        Assert.Equal((bag.ToString(), 3), (item.GetProperty("product_id").GetString(), item.GetProperty("quantity").GetInt32()));
        Assert.StartsWith("https://app.example/checkout/", cart.GetProperty("checkout_url").GetString());
        Assert.Single(_orders.Orders);

        CallToolResult unknown = await client.CallToolAsync("get_cart", new Dictionary<string, object?> { ["order_id"] = Guid.NewGuid().ToString() });
        Assert.True(unknown.IsError);
        Assert.Contains("no such cart", Text(unknown));
    }

    [Fact]
    public async Task The_agent_sees_add_to_cart_and_remove_from_cart_with_no_price_input()
    {
        await using McpClient client = await ConnectAsync();
        IList<McpClientTool> tools = await client.ListToolsAsync();

        McpClientTool add = Assert.Single(tools, t => t.Name == "add_to_cart");
        McpClientTool remove = Assert.Single(tools, t => t.Name == "remove_from_cart");

        JsonElement addItem = add.JsonSchema.GetProperty("properties").GetProperty("items").GetProperty("items");
        Assert.Equal(["product_id", "quantity", "variant_id"], addItem.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(
            ["agent_id", "connection_id", "idempotency_key", "items", "sender"],
            remove.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order());
        JsonElement removeItem = remove.JsonSchema.GetProperty("properties").GetProperty("items").GetProperty("items");
        Assert.Equal(["product_id"], removeItem.GetProperty("required").EnumerateArray().Select(p => p.GetString()));
        foreach (McpClientTool tool in new[] { add, remove })
        {
            Assert.DoesNotContain("price", tool.JsonSchema.GetRawText().Replace("prices", "").Replace("priced", ""));
        }
    }

    [Fact]
    public async Task The_agent_adds_to_and_takes_out_of_the_cart_and_the_link_stays_the_same()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m, left: 5);
        Guid purse = _catalog.Product("Beaded purse", 2500m, left: 5);
        await using McpClient client = await ConnectAsync();

        CallToolResult made = await client.CallToolAsync("add_to_cart", Arguments(new[] { new { product_id = bag, quantity = 2 } }));
        JsonElement first = JsonDocument.Parse(Text(made)).RootElement;
        Assert.Equal("cart_ready", first.GetProperty("status").GetString());

        CallToolResult added = await client.CallToolAsync("add_to_cart", Arguments(new[] { new { product_id = purse, quantity = 1 } }));
        JsonElement more = JsonDocument.Parse(Text(added)).RootElement;
        Assert.Equal(("cart_updated", first.GetProperty("checkout_url").GetString(), 10500m),
            (more.GetProperty("status").GetString(), more.GetProperty("checkout_url").GetString(), more.GetProperty("total").GetDecimal()));

        CallToolResult removed = await client.CallToolAsync("remove_from_cart", Arguments(new[] { new { product_id = bag, quantity = 1 } }));
        Assert.NotEqual(true, removed.IsError);
        Assert.Equal(6500m, JsonDocument.Parse(Text(removed)).RootElement.GetProperty("total").GetDecimal());

        CallToolResult refused = await client.CallToolAsync("remove_from_cart", Arguments(new[] { new { product_id = Guid.NewGuid() } }));
        Assert.True(refused.IsError);
        Assert.Contains("isn't in the cart", Text(refused));
        Assert.Single(_orders.Orders);
    }

    [Fact]
    public async Task A_refusal_reaches_the_agent_in_words_it_can_act_on()
    {
        Guid purse = _catalog.Product("Beaded purse", 3000m, left: 0, available: false);
        await using McpClient client = await ConnectAsync();

        CallToolResult result = await client.CallToolAsync("create_cart", Arguments(new[] { new { product_id = purse, quantity = 1 } }));

        Assert.True(result.IsError);
        Assert.Contains("Beaded purse is sold out.", Text(result));
        Assert.Empty(_provider.Calls);
    }

    [Fact]
    public async Task An_omni_agent_id_makes_a_cart_and_is_kept_on_the_order()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m, left: 5);
        await using McpClient client = await ConnectAsync();

        CallToolResult result = await client.CallToolAsync("create_cart", Arguments(new[] { new { product_id = bag, quantity = 1 } }));

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("cart_ready", JsonDocument.Parse(Text(result)).RootElement.GetProperty("status").GetString());
        Assert.Equal(OmniAgent, Assert.Single(_orders.Orders).AgentId);
    }

    [Fact]
    public async Task A_cart_needs_no_agent_id()
    {
        Guid bag = _catalog.Product("Quilted bag", 4000m, left: 5);
        await using McpClient client = await ConnectAsync();
        Dictionary<string, object?> arguments = Arguments(new[] { new { product_id = bag, quantity = 1 } });
        arguments.Remove("agent_id");

        CallToolResult result = await client.CallToolAsync("create_cart", arguments);

        Assert.NotEqual(true, result.IsError);
        Assert.Null(Assert.Single(_orders.Orders).AgentId);
    }

    private Dictionary<string, object?> Arguments(object items) => new()
    {
        ["agent_id"] = OmniAgent,
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
