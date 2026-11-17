using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Parrot.Application.Auth;

namespace Parrot.Application.Payments;

/// <summary>
/// The token in a checkout link: a JWT naming the order and its workspace, valid until the
/// checkout expires. Signed with the session key but issued for its own audience, so it is
/// never accepted as a session, and a session or invite is never accepted as a checkout.
/// It carries no prices or items: those stay on the order, so the order's status (cancelled,
/// expired, paid) decides what the link can still do. The same order always gets the same
/// token, so the agent can be given the link again without storing it.
/// </summary>
public sealed class CheckoutTokens(IOptions<JwtSettings> settings)
{
    public const string Audience = "ParrotCheckout";

    private const string OrderClaim = "ord";
    private const string WorkspaceClaim = "wid";

    private readonly JwtSettings _settings = settings.Value;

    public string Issue(Guid orderId, string workspaceId, DateTime expiresAt)
    {
        SigningCredentials credentials = new(Key(), SecurityAlgorithms.HmacSha256);
        JwtSecurityToken token = new(
            issuer: _settings.Issuer,
            audience: Audience,
            claims: [new Claim(OrderClaim, orderId.ToString()), new Claim(WorkspaceClaim, workspaceId)],
            notBefore: null,
            expires: DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// The order and workspace a token names, when it is one of ours. Expiry isn't checked here:
    /// an expired link still names its order, so the page can say it expired rather than that it
    /// was never valid. The order's own expiry is what's enforced.
    /// </summary>
    public (Guid OrderId, string WorkspaceId)? Read(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 2048)
        {
            return null;
        }

        try
        {
            ClaimsPrincipal principal = new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = _settings.Issuer,
                ValidateAudience = true,
                ValidAudience = Audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = Key(),
                ValidateLifetime = false,
            }, out _);

            return Guid.TryParse(principal.FindFirstValue(OrderClaim), out Guid id) && principal.FindFirstValue(WorkspaceClaim) is { Length: > 0 } workspace
                ? (id, workspace)
                : null;
        }
        catch (Exception ex) when (ex is SecurityTokenException or ArgumentException)
        {
            return null;
        }
    }

    private SymmetricSecurityKey Key() => new(Encoding.UTF8.GetBytes(_settings.Secret));
}
