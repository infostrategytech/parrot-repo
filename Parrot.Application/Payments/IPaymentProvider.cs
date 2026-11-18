namespace Parrot.Application.Payments;

/// <summary>Makes payment links at the payment provider.</summary>
public interface IPaymentProvider
{
    /// <summary>The provider's name, stored on each order.</summary>
    string Name { get; }

    /// <summary>Whether its keys and addresses are set. Without them no link can be made.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// A link for <paramref name="amountMinor"/> (kobo) under our <paramref name="reference"/>. After paying, the
    /// customer is sent to <paramref name="returnUrl"/> (their checkout), or to the configured callback when it's null.
    /// </summary>
    /// <exception cref="PaymentProviderException">The provider refused, failed or couldn't be reached.</exception>
    Task<PaymentLink> CreateLinkAsync(string reference, long amountMinor, string description, string? returnUrl, CancellationToken ct);
}

/// <param name="SessionId">The provider's id for the payment.</param>
/// <param name="Url">Where the customer pays.</param>
public sealed record PaymentLink(string SessionId, string Url);

/// <summary>The provider couldn't make the link. The message is for logs and support, not customers.</summary>
public sealed class PaymentProviderException(string message, string? code = null) : Exception(message)
{
    /// <summary>The provider's own code, when it gave one.</summary>
    public string? Code { get; } = code;
}
