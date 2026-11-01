using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Parrot.Application.Auth;

namespace Parrot.Application.Teams;

/// <summary>
/// The token in an invite link: a JWT, so anyone holding the link (the page,
/// the person) can read when it expires and count the days left. It is
/// signed with the session key but issued for its own audience, so the API
/// never accepts it as a session and a session is never accepted as an
/// invite. It names only the invite: the address and the grants stay on the
/// server, and the invite row keeps the token's hash, so withdrawing or
/// resending an invite stops the old link even before it expires.
/// </summary>
public sealed class InviteTokens(IOptions<JwtSettings> settings, TimeProvider clock)
{
    /// <summary>The audience an invite token is issued for. Never the session audience.</summary>
    public const string Audience = "ParrotInvite";

    private const string InviteClaim = "inv";

    private readonly JwtSettings _settings = settings.Value;

    public string Issue(Guid inviteId, DateTime expiresAt)
    {
        SigningCredentials credentials = new(Key(), SecurityAlgorithms.HmacSha256);
        JwtSecurityToken token = new(
            issuer: _settings.Issuer,
            audience: Audience,
            claims: [new Claim(InviteClaim, inviteId.ToString()), new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())],
            notBefore: clock.GetUtcNow().UtcDateTime,
            expires: expiresAt,
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// The invite a token names, when it is one of ours. Expiry is not checked
    /// here: an expired link still names its invite, so the page can say it
    /// expired rather than that it was never valid.
    /// </summary>
    public Guid? Read(string token)
    {
        try
        {
            ClaimsPrincipal principal = new JwtSecurityTokenHandler().ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = _settings.Issuer,
                ValidateAudience = true,
                ValidAudience = Audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = Key(),
                ValidateLifetime = false,
            }, out _);

            return Guid.TryParse(principal.FindFirstValue(InviteClaim), out Guid id) ? id : null;
        }
        catch (Exception ex) when (ex is SecurityTokenException or ArgumentException)
        {
            return null;
        }
    }

    private SymmetricSecurityKey Key() => new(Encoding.UTF8.GetBytes(_settings.Secret));
}
