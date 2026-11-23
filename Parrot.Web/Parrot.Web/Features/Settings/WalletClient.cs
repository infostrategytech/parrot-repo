using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Parrot.Web.Features.Auth.State;
using Parrot.Web.Services;

namespace Parrot.Web.Features.Settings;

/// <summary>The workspace's wallet, from GET api/payments/wallet, in naira.</summary>
public sealed record Wallet(decimal Balance, string Currency, decimal TotalIn, decimal TotalOut, DateTime? UpdatedAt);

/// <summary>
/// One movement in the wallet. Direction is "Credit" (money in) or "Debit" (money out); Kind is "Payment", "Fee",
/// "Payout" or "Adjustment". BalanceAfter is the balance just after it.
/// </summary>
public sealed record WalletEntry(
    Guid Id,
    string Direction,
    string Kind,
    decimal Amount,
    decimal BalanceAfter,
    string Currency,
    string Description,
    string Reference,
    Guid? OrderId,
    string? ProviderTransactionId,
    DateTime CreatedAt)
{
    public bool IsCredit => string.Equals(Direction, "Credit", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A page of the wallet's movements, newest first.</summary>
public sealed record WalletEntries(IReadOnlyList<WalletEntry> Items, int Page, int PageSize, int Total)
{
    public int Pages => Math.Max(1, (Total + PageSize - 1) / Math.Max(PageSize, 1));
}

/// <summary>
/// The session's workspace's wallet and its ledger. Failures throw an <see cref="ApiException"/> or an
/// <see cref="HttpRequestException"/> (a 403 for someone who may not see it).
/// </summary>
public sealed class WalletClient(HttpClient http, AuthState authState)
{
    public const int PageSize = 20;

    public async Task<Wallet> ReadAsync()
    {
        using HttpRequestMessage request = Authorized("api/payments/wallet");
        using HttpResponseMessage response = await http.SendAsync(request);
        return await ReadAsync<Wallet>(response);
    }

    /// <summary>A page of movements; <paramref name="direction"/> "Credit" or "Debit" for one way only, null for both.</summary>
    public async Task<WalletEntries> EntriesAsync(int page, string? direction = null)
    {
        string path = $"api/payments/wallet/transactions?page={page.ToString(CultureInfo.InvariantCulture)}&pageSize={PageSize.ToString(CultureInfo.InvariantCulture)}";
        if (!string.IsNullOrWhiteSpace(direction)) path += "&direction=" + Uri.EscapeDataString(direction);
        using HttpRequestMessage request = Authorized(path);
        using HttpResponseMessage response = await http.SendAsync(request);
        return await ReadAsync<WalletEntries>(response);
    }

    private HttpRequestMessage Authorized(string path)
    {
        string token = authState.CurrentUser?.Token
            ?? throw new InvalidOperationException("The wallet is only reachable while signed in.");

        HttpRequestMessage request = new(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        await response.EnsureApiSuccessAsync();
        return await response.Content.ReadFromJsonAsync<T>()
            ?? throw new HttpRequestException("The server returned an empty response.");
    }
}

/// <summary>How the Wallet section words amounts and movements.</summary>
public static class WalletText
{
    /// <summary>"₦8,000.00" for naira, "USD 8,000.00" otherwise.</summary>
    public static string Money(decimal amount, string? currency)
    {
        string number = amount.ToString("#,##0.00", CultureInfo.InvariantCulture);
        string code = string.IsNullOrWhiteSpace(currency) ? "NGN" : currency.Trim().ToUpperInvariant();
        return code == "NGN" ? "₦" + number : $"{code} {number}";
    }

    /// <summary>"+₦8,000.00" for money in, "−₦500.00" for money out.</summary>
    public static string Signed(WalletEntry entry) => (entry.IsCredit ? "+" : "−") + Money(entry.Amount, entry.Currency);

    public static string KindLabel(string? kind) => kind switch
    {
        "Payment" => "Customer payment",
        "Fee" => "Fee",
        "Payout" => "Payout",
        "Adjustment" => "Adjustment",
        _ => kind ?? "",
    };

    /// <summary>"23 Nov 2026, 14:05", in the viewer's time zone.</summary>
    public static string When(DateTime utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone).ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture);
}
