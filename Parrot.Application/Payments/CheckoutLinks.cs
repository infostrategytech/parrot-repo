using Microsoft.Extensions.Options;
using Parrot.Application.Email;
using Parrot.Domain.Entities;

namespace Parrot.Application.Payments;

/// <summary>The checkout page's address for an order: the web app's /checkout/{token}.</summary>
public sealed class CheckoutLinks(CheckoutTokens tokens, IOptions<PaymentSettings> payments, IOptions<EmailSettings> email)
{
    /// <summary>The web app's base address, or null when none is configured.</summary>
    public string? BaseUrl
    {
        get
        {
            string? configured = string.IsNullOrWhiteSpace(payments.Value.CheckoutBaseUrl) ? email.Value.BaseUrl : payments.Value.CheckoutBaseUrl;
            return Uri.TryCreate(configured, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
                ? configured!.TrimEnd('/')
                : null;
        }
    }

    public bool IsConfigured => BaseUrl is not null;

    /// <summary>The link for an order's checkout. The same order always gets the same link.</summary>
    public string For(PaymentOrder order)
    {
        string token = tokens.Issue(order.Id, order.WorkspaceId, order.CheckoutExpiresAt ?? order.ExpiresAt ?? order.CreatedAt);
        return $"{BaseUrl ?? throw new InvalidOperationException("No checkout base URL is configured.")}/checkout/{token}";
    }

    public (Guid OrderId, string WorkspaceId)? Read(string? token) => tokens.Read(token);
}
