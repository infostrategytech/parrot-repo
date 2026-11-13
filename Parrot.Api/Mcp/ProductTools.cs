using System.ComponentModel;
using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Parrot.Application.Agents;
using Parrot.Application.Auth;

namespace Parrot.Api.Mcp;

/// <summary>
/// The agent's product tools: live prices and stock, a keyword search to fall back on, and
/// a plain listing for "what do you have?", which a natural-language index can't answer.
/// The workspace always comes from the agent's service token (its <c>tenant_id</c>), never
/// from the arguments, so an agent can only ever see its own business's catalog.
/// Answers are the catalog's own, passed through: product text in them is marked as
/// tenant-entered data, not instructions.
/// </summary>
[McpServerToolType]
public sealed class ProductTools(ICatalogToolsClient catalog, IHttpContextAccessor http, ILogger<ProductTools> logger)
{
    public const int MaxPriceItems = 20;

    [McpServerTool(Name = "get_prices", Title = "Live prices and stock", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Live price, availability and quantity left for up to 20 of this business's products at once: pass their ids as an array, "
        + "such as the ones your search found. Call it before telling a customer any price, stock level or that an item is available: "
        + "these change, so never quote them from memory. "
        + "To find what fits a budget, pass min_price and/or max_price: items then holds only the products in that range, and closest "
        + "the others, nearest to the range first; when items is empty, offer from closest. "
        + "quantity_left is how many are left (null when the business doesn't count them; 0 when sold out). "
        + "price_on_request means the business quotes it; say so and never guess. Never offer products listed in not_on_sale.")]
    public async Task<JsonElement> GetPrices(
        [Description("Product ids, from your search: an array of 1 to 20.")] Guid[] ids,
        [Description("Lowest price wanted, in the business's currency (naira unless the result says otherwise). Leave out when there's no lower limit.")] decimal? min_price = null,
        [Description("Highest price wanted: the customer's budget, in the business's currency. Leave out when there's no upper limit.")] decimal? max_price = null,
        CancellationToken ct = default)
    {
        if (ids is not { Length: > 0 })
        {
            throw new McpException("Pass at least one product id in ids.");
        }

        if (ids.Length > MaxPriceItems)
        {
            throw new McpException($"Pass at most {MaxPriceItems} product ids at once.");
        }

        if (min_price > max_price)
        {
            throw new McpException("min_price can't be more than max_price.");
        }

        return await CallAsync("get_prices", new { Ids = ids, MinPrice = min_price, MaxPrice = max_price }, ct);
    }

    [McpServerTool(Name = "search_products", Title = "Keyword search", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Keyword search in this business's live catalog. It matches the exact words in product names and descriptions, so it misses "
        + "synonyms and photos: search your own product index first. Use this when your index finds nothing, or for a product added in the last few minutes. "
        + "Then call get_prices with the ids you want to offer.")]
    public async Task<JsonElement> SearchProducts(
        [Description("Words to look for, such as \"bag\" or \"leather tote\".")] string query,
        [Description("How many products to return, 1 to 10. Defaults to 10.")] int? limit = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new McpException("Pass the words to search for in query.");
        }

        return await CallAsync("search_catalog", new { Query = query.Trim(), Limit = ClampLimit(limit) }, ct);
    }

    [McpServerTool(Name = "list_products", Title = "Browse the catalog", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Lists this business's products, newest first, straight from the live catalog. Use it when the customer asks what's available "
        + "or wants to see options without naming anything, such as \"what do you have?\" or \"show me your products\". "
        + "When next_cursor is set there are more: pass it back as cursor to show the next ones when the customer asks for more. "
        + "Then call get_prices with the ids you want to offer.")]
    public async Task<JsonElement> ListProducts(
        [Description("How many products to return, 1 to 10. Defaults to 10.")] int? limit = null,
        [Description("next_cursor from the previous list_products answer, to get the next page. Leave out for the first page.")] string? cursor = null,
        CancellationToken ct = default)
    {
        return await CallAsync("search_catalog", new { Limit = ClampLimit(limit), Cursor = string.IsNullOrWhiteSpace(cursor) ? null : cursor.Trim() }, ct);
    }

    private static int? ClampLimit(int? limit) => limit is null ? null : Math.Clamp(limit.Value, 1, 10);

    private async Task<JsonElement> CallAsync(string tool, object arguments, CancellationToken ct)
    {
        string workspace = Workspace();
        Stopwatch watch = Stopwatch.StartNew();
        try
        {
            JsonElement result = await catalog.CallAsync(workspace, tool, arguments, ct);
            logger.LogInformation("MCP tool answered. Tool={Tool} Workspace={Workspace} ElapsedMs={ElapsedMs}", tool, workspace, watch.ElapsedMilliseconds);
            return result;
        }
        catch (CatalogToolException ex)
        {
            logger.LogWarning("MCP tool failed. Tool={Tool} Workspace={Workspace} Refused={Refused} Reason={Reason}", tool, workspace, ex.Refused, ex.Message);

            // A refusal the agent can fix is passed on; an outage tells it not to make things up.
            throw new McpException(ex.Refused
                ? ex.Message
                : $"{ex.Message} Don't quote prices or stock until it answers; tell the customer you'll check and get back to them.");
        }
    }

    /// <summary>The workspace the agent's service token was minted for.</summary>
    private string Workspace()
    {
        ClaimsPrincipal? user = http.HttpContext?.User;
        return user?.FindFirstValue(ServiceScopes.TenantClaim) is { Length: > 0 } tenant
            ? tenant
            : throw new McpException("The token names no workspace.");
    }
}
