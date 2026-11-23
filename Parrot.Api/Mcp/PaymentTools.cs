using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Parrot.Application.Auth;
using Parrot.Application.Payments;

namespace Parrot.Api.Mcp;

/// <summary>
/// The agent's payment tools: the cart a customer agreed to buy, and where it's got
/// to. The agent names products and quantities only; Parrot prices them from the live catalog.
/// The customer pays from the checkout page. The workspace comes from the agent's service
/// token, never from the arguments, so an agent only ever makes and reads its own business's orders.
/// </summary>
[McpServerToolType]
public sealed class PaymentTools(CheckoutService checkouts, PaymentConfirmationService confirmations, IHttpContextAccessor http, ILogger<PaymentTools> logger)
{
    private static readonly JsonSerializerOptions Json = new () { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    [McpServerTool(Name = "create_cart", Title = "Cart", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description(
        "Sets the customer's cart to exactly the items you pass, priced from the live catalog, and returns checkout_url to send them. "
        + "On that page the customer sees the items and the latest prices and presses Pay, which opens the payment page. "
        + "Always pass the COMPLETE cart: every item the customer wants with its full quantity, not only what changed. Anything you leave out is taken out. "
        + "Each conversation has one cart. While the customer hasn't pressed Pay, calling again changes that cart and keeps the same checkout_url (status cart_updated), "
        + "so tell them what changed rather than sending a new link. Once they've pressed Pay, calling again makes a new cart and link (status cart_ready) that replaces the old one. "
        + "Only call it once the customer has confirmed the items, options and quantities, and you've checked them with get_prices. "
        + "Pass product ids and quantities, never prices: the total is worked out here. For a product with options (sizes, colours), pass the chosen option's variant_id. "
        + "Send the customer checkout_url with the total and say until when it's open (expires_at). "
        + "If a price changes before they pay, the page shows the old and the new price, so don't promise a price will hold. "
        + "If it's refused, the reason says what to fix (an item sold out, not enough left, an option to choose), and the cart stays as it was: sort it out with the customer, then call again. "
        + "Never tell the customer an order is paid; use get_payment_status.")]
    public async Task<JsonElement> CreateCart(
        [Description("Your agent id: the one you raise tasks with.")] Guid agent_id,
        [Description("The conversation's connection id.")] Guid connection_id,
        [Description("The customer's sender id in this conversation.")] string sender,
        [Description("Everything in the cart, complete: 1 to 20 items, each with its full quantity.")] PaymentItemArgument[] items,
        [Description("A key you make up for each call, such as a UUID. Sending the same call again with the same key gives the same answer, so a retry never makes a second cart. Use a new key for each change.")] string idempotency_key,
        [Description("The customer's name, if you know it.")] string? customer_name = null,
        [Description("The customer's email, if they gave it.")] string? customer_email = null,
        [Description("The customer's phone number, if they gave it.")] string? customer_phone = null,
        [Description("A note for the business about the order, such as where to deliver. Not shown to the customer.")] string? note = null,
        CancellationToken ct = default)
    {
        string workspace = Workspace();
        CreateCheckoutRequest request = new (
            agent_id,
            connection_id,
            sender,
            (items ?? Array.Empty<PaymentItemArgument>()).Select(i => new CheckoutItem(i.ProductId, i.VariantId, i.Quantity)).ToArray(),
            idempotency_key,
            customer_name,
            customer_email,
            customer_phone,
            note);
        try
        {
            CheckoutResult result = await checkouts.CreateAsync(workspace, request, ct);
            return JsonSerializer.SerializeToElement(result, Json);
        }
        catch (CheckoutRefusedException ex)
        {
            logger.LogInformation("Cart refused. Workspace={Workspace} Reason={Reason}", workspace, ex.Message);
            throw new McpException(ex.Message);
        }
        catch (CheckoutUnavailableException ex)
        {
            logger.LogWarning("Cart unavailable. Workspace={Workspace} Reason={Reason}", workspace, ex.Message);
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "get_cart", Title = "Cart", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "What's in the customer's cart now and where it's got to. Call it before changing a cart, when the customer asks what they've ordered or what it costs, "
        + "or when you don't remember what's in it. Pass order_id, or leave it out and pass connection_id and sender for the conversation's newest cart. "
        + "state is open (can_pay says whether it can be paid as it is; problem says why not), paying (they pressed Pay), paid, expired, replaced or unavailable. "
        + "While it's open, each item is priced again from the live catalog, as the customer's page shows it: price_changed and quoted_unit_price show what moved. "
        + "items carry the product_id and variant_id to pass create_cart, which always takes the complete cart. "
        + "for_the_agent says what to do next. It doesn't say whether money arrived: use get_payment_status for that.")]
    public async Task<JsonElement> GetCart(
        // A string, so a wrong value gets a clear answer instead of failing before the tool runs.
        [Description("The order_id create_cart returned (a UUID). Leave out to get the conversation's cart.")] string? order_id = null,
        [Description("The conversation's connection id, when order_id isn't given.")] Guid? connection_id = null,
        [Description("The customer's sender id in this conversation, when order_id isn't given.")] string? sender = null,
        CancellationToken ct = default)
    {
        try
        {
            return JsonSerializer.SerializeToElement(await checkouts.GetCartAsync(Workspace(), order_id, connection_id, sender, ct), Json);
        }
        catch (CheckoutRefusedException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "get_payment_status", Title = "Payment status", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Whether a checkout has been paid. Call it when the customer says they've paid or asks about their order. "
        + "status is awaiting_payment (checkout_url is the link to send again if they lost it), paid, paid_needs_attention, expired, replaced or not_sent; "
        + "for_the_agent says what to tell the customer. "
        + "Only a paid status means the money arrived: never confirm a payment on the customer's word.")]
    public async Task<JsonElement> GetPaymentStatus(
        // A string, so a wrong value gets a clear answer instead of failing before the tool runs.
        [Description("The order_id create_cart returned (a UUID). The order's prt_ reference also works.")] string order_id,
        CancellationToken ct = default)
    {
        try
        {
            return JsonSerializer.SerializeToElement(await confirmations.GetStatusAsync(Workspace(), order_id, ct), Json);
        }
        catch (CheckoutRefusedException ex)
        {
            throw new McpException(ex.Message);
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

/// <summary>One item in create_cart.</summary>
public sealed record PaymentItemArgument(
    [property: JsonPropertyName("product_id"), Description("The product's id.")] Guid ProductId,
    [property: JsonPropertyName("quantity"), Description("How many, 1 to 999.")] int Quantity,
    [property: JsonPropertyName("variant_id"), Description("The chosen option's id, for a product with options. Leave out otherwise.")] Guid? VariantId = null);
