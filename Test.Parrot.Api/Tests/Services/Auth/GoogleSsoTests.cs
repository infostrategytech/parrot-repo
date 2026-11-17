using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Parrot.Api.Controllers;
using Parrot.Application.Auth;
using Parrot.Application.DTOs.Auth;
using Parrot.Application.Logging;

namespace Test.Parrot.Api.Tests.Services.Auth;

// "Continue with Google": always on (GoogleOAuth:ClientId is required), so the happy path, missing claims,
// and a cancelled exchange all have dedicated tests — mirroring FacebookSsoTests.
public class GoogleSsoTests
{
    private const string Frontend = "https://app.example.test";

    private readonly Mock<IAuthService> _auth = new();
    private readonly Mock<IAuthenticationSchemeProvider> _schemes = new();
    private readonly Mock<IAuthenticationService> _authentication = new();
    private readonly Mock<IAppLogger<SsoController>> _logger = new();

    private SsoController Create(ClaimsPrincipal? external = null)
    {
        _authentication.Setup(a => a.AuthenticateAsync(It.IsAny<HttpContext>(), "ExternalCookies"))
            .ReturnsAsync(external is null
                ? AuthenticateResult.Fail("cancelled")
                : AuthenticateResult.Success(new AuthenticationTicket(external, "ExternalCookies")));
        ServiceProvider services = new ServiceCollection().AddSingleton(_authentication.Object).BuildServiceProvider();
        Mock<IUrlHelper> url = new();
        url.Setup(u => u.Action(It.IsAny<UrlActionContext>())).Returns("/api/auth/sso/google/callback");
        return new SsoController(_auth.Object, Options.Create(new FrontendSettings { BaseUrl = Frontend }), _schemes.Object, _logger.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = services } },
            Url = url.Object,
        };
    }

    private static ClaimsPrincipal Person(string? email) => new(new ClaimsIdentity(
        new[] { new Claim(ClaimTypes.NameIdentifier, "google-456") }
            .Concat(email is null ? [] : [new Claim(ClaimTypes.Email, email)]), "Google"));

    [Fact]
    public void InitiateGoogle_ChallengesWithGoogleScheme_AndSetsCallbackUri()
    {
        IActionResult result = Create().InitiateGoogle();

        ChallengeResult challenge = Assert.IsType<ChallengeResult>(result);
        Assert.Equal([GoogleDefaults.AuthenticationScheme], challenge.AuthenticationSchemes);
        Assert.Equal("/api/auth/sso/google/callback", challenge.Properties!.RedirectUri);
    }

    [Fact]
    public async Task AGoogleSignIn_FindsOrCreatesTheAccountByEmail_AndHandsOverTheSession()
    {
        _auth.Setup(a => a.ExternalLoginAsync("ada@example.test", "Google", "google-456"))
            .ReturnsAsync(new AuthResponse("token", DateTime.UtcNow.AddHours(1), "user-1", "ada@example.test", null, null, null, "user-1", "Owner", ["*"]));

        IActionResult result = await Create(Person("ada@example.test")).GoogleCallback();

        Assert.StartsWith($"{Frontend}/sso-callback?token=token", Assert.IsType<RedirectResult>(result).Url);
    }

    [Fact]
    public async Task AGoogleAccountWithNoEmail_IsSentBackWithAMessage_AndNoAccountIsMade()
    {
        IActionResult result = await Create(Person(email: null)).GoogleCallback();

        Assert.Equal($"{Frontend}/login?error=sso_missing_claims", Assert.IsType<RedirectResult>(result).Url);
        _auth.Verify(a => a.ExternalLoginAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ACancelledGoogleSignIn_IsSentBackWithAMessage()
    {
        IActionResult result = await Create(external: null).GoogleCallback();

        Assert.Equal($"{Frontend}/login?error=sso_failed", Assert.IsType<RedirectResult>(result).Url);
    }

    [Fact]
    public async Task FacebookKeepsItsOwnErrors()
    {
        IActionResult result = await Create(Person(email: null)).FacebookCallback();

        Assert.Equal($"{Frontend}/login?error=facebook_no_email", Assert.IsType<RedirectResult>(result).Url);
    }
}
