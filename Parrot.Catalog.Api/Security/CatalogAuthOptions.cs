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

    public bool IsOidc => string.Equals(Mode, "Oidc", StringComparison.OrdinalIgnoreCase);
}
