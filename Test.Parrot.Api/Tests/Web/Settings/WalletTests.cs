using System.Net;
using System.Net.Http.Json;
using Parrot.SharedComponents.Layout;
using Parrot.Web.Components.Pages.Dashboard.Settings;
using Parrot.Web.Features.Auth.Models;
using Parrot.Web.Features.Auth.State;
using Parrot.Web.Features.Settings;

namespace Test.Parrot.Api.Tests.Web.Settings;

// Controlled contract responses only: the wallet client's requests, and how the Wallet section words money and movements.
public class WalletTests
{
    private static readonly object Balance = new { balance = 3000.50m, currency = "NGN", totalIn = 3500.50m, totalOut = 500m, updatedAt = "2026-11-23T10:00:00Z" };

    private static readonly object Entries = new
    {
        items = new object[]
        {
            new
            {
                id = Guid.NewGuid(), direction = "Debit", kind = "Payout", amount = 500m, balanceAfter = 3000.50m, currency = "NGN",
                description = "Paid out to the business", reference = "payout-1", orderId = (Guid?)null, providerTransactionId = (string?)null,
                createdAt = "2026-11-23T10:00:00Z",
            },
            new
            {
                id = Guid.NewGuid(), direction = "Credit", kind = "Payment", amount = 2500.50m, balanceAfter = 3500.50m, currency = "NGN",
                description = "Payment from Ada for 1 x Zobo (50cl)", reference = "prt_abc", orderId = (Guid?)Guid.NewGuid(), providerTransactionId = "txn-1",
                createdAt = "2026-11-23T09:00:00Z",
            },
        },
        page = 1, pageSize = 20, total = 41,
    };

    [Fact]
    public async Task ReadingTheBalanceSendsTheSessionToken()
    {
        var (client, handler) = Client(Balance);
        var wallet = await client.ReadAsync();
        Assert.Equal("/api/payments/wallet", handler.Path);
        Assert.Equal("controlled-test-token", handler.Token);
        Assert.Equal((3000.50m, 3500.50m, 500m), (wallet.Balance, wallet.TotalIn, wallet.TotalOut));
    }

    [Fact]
    public async Task TransactionsAreAskedForAPageAndOneWayOnly()
    {
        var (client, handler) = Client(Entries);

        var page = await client.EntriesAsync(2, "Credit");

        Assert.Equal("/api/payments/wallet/transactions?page=2&pageSize=20&direction=Credit", handler.Path);
        Assert.Equal((41, 3), (page.Total, page.Pages));
        Assert.Equal([false, true], page.Items.Select(i => i.IsCredit));

        await client.EntriesAsync(1);
        Assert.Equal("/api/payments/wallet/transactions?page=1&pageSize=20", handler.Path);
    }

    [Fact]
    public async Task SomeoneWhoMayNotSeeItGetsAForbidden()
    {
        var (client, handler) = Client(new { errorCode = "FORBIDDEN", message = "No." });
        handler.Status = HttpStatusCode.Forbidden;
        var error = await Assert.ThrowsAnyAsync<HttpRequestException>(client.ReadAsync);
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
    }

    [Fact]
    public void MoneyReadsWithItsSignAndInNaira()
    {
        WalletEntry payment = new(Guid.NewGuid(), "Credit", "Payment", 8000m, 8000m, "NGN", "x", "prt_1", null, null, DateTime.UtcNow);
        WalletEntry payout = payment with { Direction = "Debit", Kind = "Payout", Amount = 1500.5m };

        Assert.Equal("+₦8,000.00", WalletText.Signed(payment));
        Assert.Equal("−₦1,500.50", WalletText.Signed(payout));
        Assert.Equal("USD 12.00", WalletText.Money(12m, "usd"));
        Assert.Equal("Customer payment", WalletText.KindLabel("Payment"));
    }

    [Fact]
    public void TimesShowInTheViewersZone()
    {
        TimeZoneInfo lagos = TimeZoneInfo.CreateCustomTimeZone("WAT", TimeSpan.FromHours(1), "WAT", "WAT");
        Assert.Equal("23 Nov 2026, 10:05", WalletText.When(new DateTime(2026, 11, 23, 9, 5, 0, DateTimeKind.Utc), lagos));
    }

    [Fact]
    public void OnlyTheOwnerSeesTheSection()
    {
        var section = SettingsSections.Business.Single(s => s.Id == "wallet");
        Assert.Equal(AccessRules.Business, section.Rule);
        AuthResponse manager = new() { Role = "Conversation Manager", Permissions = ["conversations.view"] };
        Assert.DoesNotContain(SettingsSections.GroupsFor(manager.Allows).SelectMany(g => g.Items), item => item.Id == "wallet");
    }

    private static (WalletClient Client, ControlledHandler Handler) Client(object reply)
    {
        var auth = new AuthState();
        auth.SetUser(new AuthResponse { Token = "controlled-test-token", UserId = "test-owner", WorkspaceId = "test-workspace", ExpiresAt = DateTime.UtcNow.AddHours(1) });
        var handler = new ControlledHandler(reply);
        return (new WalletClient(new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") }, auth), handler);
    }

    private sealed class ControlledHandler(object reply) : HttpMessageHandler
    {
        public string? Path, Token;
        public HttpStatusCode Status = HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Path = request.RequestUri!.PathAndQuery;
            Token = request.Headers.Authorization?.Parameter;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = JsonContent.Create(reply) });
        }
    }
}
