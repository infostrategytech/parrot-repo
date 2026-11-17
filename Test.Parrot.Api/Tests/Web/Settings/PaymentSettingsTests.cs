using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Parrot.SharedComponents.Layout;
using Parrot.Web.Components.Pages.Dashboard.Settings;
using Parrot.Web.Features.Auth.Models;
using Parrot.Web.Features.Auth.State;
using Parrot.Web.Features.Settings;
using Parrot.Web.Services;

namespace Test.Parrot.Api.Tests.Web.Settings;

// Controlled contract responses only: the client's requests and how the section words its choices.
public class PaymentSettingsTests
{
    private static readonly object Saved = new
    {
        checkoutMinutes = 60, holdMode = "AtCheckout", paymentMinutes = 15, priceChangeRule = "Block", isDefault = false,
        updatedAt = "2026-11-17T10:00:00Z", limits = new { minCheckoutMinutes = 5, maxCheckoutMinutes = 1440, minPaymentMinutes = 5, maxPaymentMinutes = 120 },
    };

    [Fact]
    public async Task ReadingSendsTheSessionTokenAndReadsTheStringChoices()
    {
        var (client, handler) = Client(Saved);
        var settings = await client.ReadAsync();
        Assert.Equal("/api/payments/settings", handler.Path);
        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal("controlled-test-token", handler.Token);
        Assert.Equal(60, settings.CheckoutMinutes);
        Assert.Equal("AtCheckout", settings.HoldMode);
        Assert.Equal("Block", settings.PriceChangeRule);
        Assert.Equal(1440, settings.Limits!.MaxCheckoutMinutes);
    }

    [Fact]
    public async Task SavingSendsOnlyTheFourChoices()
    {
        var (client, handler) = Client(Saved);
        await client.SaveAsync(new PaymentSettings(60, "AtCheckout", 15, "Block", IsDefault: true, Limits: PaymentLimits.Defaults));
        Assert.Equal(HttpMethod.Put, handler.Method);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(["checkoutMinutes", "holdMode", "paymentMinutes", "priceChangeRule"], body.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("AtCheckout", body.RootElement.GetProperty("holdMode").GetString());
    }

    [Fact]
    public async Task AValueOutOfRangeShowsTheServersMessage()
    {
        var (client, handler) = Client(new { errorCode = "VALIDATION_ERROR", message = "Checkout links last 5 to 1440 minutes." });
        handler.Status = HttpStatusCode.BadRequest;
        var error = await Assert.ThrowsAsync<ApiException>(() => client.SaveAsync(new PaymentSettings(2, "AtPay", 15, "ConfirmNewPrice")));
        Assert.Equal("VALIDATION_ERROR", error.ErrorCode);
        Assert.Equal("Checkout links last 5 to 1440 minutes.", error.UserMessage);
    }

    [Fact]
    public async Task SignedOutNothingIsSent()
    {
        var handler = new ControlledHandler(Saved);
        var client = new PaymentSettingsClient(new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") }, new AuthState());
        await Assert.ThrowsAsync<InvalidOperationException>(client.ReadAsync);
        Assert.Null(handler.Path);
    }

    [Fact]
    public void ThePresetsStayInsideTheLimitsAndKeepAValueSetElsewhere()
    {
        Assert.Equal([15, 30, 60, 120, 360, 720, 1440], PaymentChoices.Options(PaymentChoices.CheckoutPresets, 5, 1440, 30));
        Assert.Equal([15, 30, 45, 60], PaymentChoices.Options(PaymentChoices.CheckoutPresets, 5, 60, 45));
        Assert.DoesNotContain(2, PaymentChoices.Options(PaymentChoices.PaymentPresets, 5, 120, 2));
    }

    [Theory]
    [InlineData(5, "5 minutes")]
    [InlineData(60, "1 hour")]
    [InlineData(90, "1 hour 30 minutes")]
    [InlineData(1440, "24 hours")]
    public void DurationsReadAsWords(int minutes, string expected) => Assert.Equal(expected, PaymentChoices.Duration(minutes));

    [Fact]
    public void OnlyTheOwnerSeesTheSection()
    {
        var section = SettingsSections.Business.Single(s => s.Id == "payments");
        Assert.Equal(AccessRules.Business, section.Rule);
        AuthResponse manager = new() { Role = "Conversation Manager", Permissions = ["conversations.view"] };
        Assert.DoesNotContain(SettingsSections.GroupsFor(manager.Allows).SelectMany(g => g.Items), item => item.Id == "payments");
    }

    private static (PaymentSettingsClient Client, ControlledHandler Handler) Client(object reply)
    {
        var auth = new AuthState();
        auth.SetUser(new AuthResponse { Token = "controlled-test-token", UserId = "test-owner", WorkspaceId = "test-workspace", ExpiresAt = DateTime.UtcNow.AddHours(1) });
        var handler = new ControlledHandler(reply);
        return (new PaymentSettingsClient(new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") }, auth), handler);
    }

    private sealed class ControlledHandler(object reply) : HttpMessageHandler
    {
        public string? Path, Body, Token;
        public HttpMethod? Method;
        public HttpStatusCode Status = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Path = request.RequestUri!.PathAndQuery;
            Method = request.Method;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Token = request.Headers.Authorization?.Parameter;
            return new HttpResponseMessage(Status) { Content = JsonContent.Create(reply) };
        }
    }
}
