using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Options;
using Parrot.Application.Auth;
using Parrot.Application.Teams;
using Parrot.Application.Utils;

namespace Test.Parrot.Api.Tests.Services.Teams;

// The invite link is a JWT: readable for its expiry, but never a session, and a session is never an invite.
public class InviteTokensTests
{
    private static readonly JwtSettings Settings = new()
    {
        Secret = "a-test-signing-key-that-is-long-enough-for-hs256",
        Issuer = "ParrotApi",
        Audience = "ParrotApp",
    };

    private readonly InviteTokens _tokens = TeamFixture.InviteTokens();

    [Fact]
    public void AnInviteToken_SaysWhenItExpires_AndNamesOnlyTheInvite()
    {
        Guid id = Guid.NewGuid();
        DateTime expires = DateTime.UtcNow.AddDays(7);

        string token = _tokens.Issue(id, expires);
        JwtSecurityToken read = new JwtSecurityTokenHandler().ReadJwtToken(token);

        Assert.Equal(id, _tokens.Read(token));
        Assert.Equal(expires, read.ValidTo, TimeSpan.FromSeconds(1));
        Assert.Contains(InviteTokens.Audience, read.Audiences);
        Assert.DoesNotContain(read.Claims, c => c.Type is "email" or "sub");
    }

    [Fact]
    public void ASessionToken_IsNotAnInvite()
    {
        string session = new JwtHelper(Options.Create(Settings)).GenerateToken([new Claim("inv", Guid.NewGuid().ToString())]);

        Assert.Null(_tokens.Read(session));
    }

    [Fact]
    public void AnInviteToken_IsNotASession()
    {
        string token = _tokens.Issue(Guid.NewGuid(), DateTime.UtcNow.AddDays(7));

        // The API's own bearer settings.
        TokenValidationParameters bearer = new()
        {
            ValidIssuer = Settings.Issuer,
            ValidAudience = Settings.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Settings.Secret)),
        };
        Assert.Throws<SecurityTokenInvalidAudienceException>(() => new JwtSecurityTokenHandler().ValidateToken(token, bearer, out _));
    }

    [Fact]
    public void ATamperedToken_IsNotRead()
    {
        string token = _tokens.Issue(Guid.NewGuid(), DateTime.UtcNow.AddDays(7));

        Assert.Null(_tokens.Read(token[..^2] + "xx"));
        Assert.Null(_tokens.Read("not-a-token"));
    }

    // An expired link still names its invite, so the page can say "expired" rather than "not valid".
    [Fact]
    public void AnExpiredToken_StillNamesItsInvite()
    {
        Guid id = Guid.NewGuid();
        DateTimeOffset tenDaysAgo = DateTimeOffset.UtcNow.AddDays(-10);
        InviteTokens issuedThen = new(Options.Create(Settings), new FixedClock(tenDaysAgo));

        string expired = issuedThen.Issue(id, tenDaysAgo.UtcDateTime.AddDays(7));

        Assert.True(new JwtSecurityTokenHandler().ReadJwtToken(expired).ValidTo < DateTime.UtcNow);
        Assert.Equal(id, _tokens.Read(expired));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
