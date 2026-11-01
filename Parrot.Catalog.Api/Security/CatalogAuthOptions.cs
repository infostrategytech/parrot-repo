namespace Parrot.Catalog.Api.Security;

/// <summary>
/// "Catalog:Auth". Symmetric shares Parrot.Api's JwtSettings (falls back to that section
/// when its own values are absent); Oidc validates against the provider's JWKS (ADR-003).
/// </summary>
public sealed class CatalogAuthOptions
{
    public const string SectionName = "Catalog:Auth";
    public const string PlaceholderSecret = "YourSuperSecretKeyThatIsAtLeast32CharactersLong!";

    public string Mode { get; set; } = "Symmetric";

    public string? Secret { get; set; }

    public string? Issuer { get; set; }

    public string? Audience { get; set; }

    public string? Authority { get; set; }

    public bool RequireHttpsMetadata { get; set; } = true;

    public int ClockSkewSeconds { get; set; } = 30;

    /// <summary>
    /// Parrot API's base address. When set, every request made with a Parrot
    /// session token is checked against the member's current access there
    /// (<see cref="SessionAccessCheck"/>). Unset: the token's scopes stand.
    /// </summary>
    public string? AccessCheckUrl { get; set; }

    /// <summary>How long one answer is reused. 0, the default, asks on every request, so a change applies at once.</summary>
    public int AccessCheckCacheSeconds { get; set; }

    public bool IsOidc => string.Equals(Mode, "Oidc", StringComparison.OrdinalIgnoreCase);
}
