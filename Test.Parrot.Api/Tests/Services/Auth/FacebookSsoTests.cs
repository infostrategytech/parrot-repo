using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Facebook;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Parrot.Api.Controllers;
using Parrot.Application.Auth;
using Parrot.Application.DTOs.Auth;

namespace Test.Parrot.Api.Tests.Services.Auth;

// "Continue with Facebook": off until a Facebook app is configured, then the same ending as Google's.
public class FacebookSsoTests
{
    private const string Frontend = "https://app.example.test";

    private readonly Mock<IAuthService> _auth = new();
    private readonly Mock<IAuthenticationSchemeProvider> _schemes = new();
    private readonly Mock<IAuthenticationService> _authentication = new();

    private SsoController Create(ClaimsPrincipal? external = null)
    {
        _authentication.Setup(a => a.AuthenticateAsync(It.IsAny<HttpContext>(), "ExternalCookies"))
            .ReturnsAsync(external is null
                ? AuthenticateResult.Fail("cancelled")
                : AuthenticateResult.Success(new AuthenticationTicket(external, "ExternalCookies")));
        ServiceProvider services = new ServiceCollection().AddSingleton(_authentication.Object).BuildServiceProvider();
        Mock<IUrlHelper> url = new();
        url.Setup(u => u.Action(It.IsAny<UrlActionContext>())).Returns("/api/auth/sso/facebook/callback");
        return new SsoController(_auth.Object, Options.Create(new FrontendSettings { BaseUrl = Frontend }), _schemes.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = services } },
            Url = url.Object,
        };
    }

    private static ClaimsPrincipal Person(string? email) => new(new ClaimsIdentity(
        new[] { new Claim(ClaimTypes.NameIdentifier, "fb-123") }
            .Concat(email is null ? [] : [new Claim(ClaimTypes.Email, email)]), "Facebook"));

    [Fact]
    public async Task WithoutAFacebookApp_TheLoginPageSaysItIsNotAvailable()
    {
        IActionResult result = await Create().InitiateFacebook();

        Assert.Equal($"{Frontend}/login?error=facebook_unavailable", Assert.IsType<RedirectResult>(result).Url);
    }

    [Fact]
    public async Task WithAFacebookApp_TheBrowserGoesToFacebook()
    {
        _schemes.Setup(s => s.GetSchemeAsync(FacebookDefaults.AuthenticationScheme))
            .ReturnsAsync(new AuthenticationScheme(FacebookDefaults.AuthenticationScheme, null, typeof(IAuthenticationHandler)));

        IActionResult result = await Create().InitiateFacebook();

        ChallengeResult challenge = Assert.IsType<ChallengeResult>(result);
        Assert.Equal([FacebookDefaults.AuthenticationScheme], challenge.AuthenticationSchemes);
        Assert.Equal("/api/auth/sso/facebook/callback", challenge.Properties!.RedirectUri);
    }

    [Fact]
    public async Task AFacebookSignIn_FindsOrCreatesTheAccountByEmail_AndHandsOverTheSession()
    {
        _auth.Setup(a => a.ExternalLoginAsync("ada@example.test", "Facebook", "fb-123"))
            .ReturnsAsync(new AuthResponse("token", DateTime.UtcNow.AddHours(1), "user-1", "ada@example.test", null, null, null));

        IActionResult result = await Create(Person("ada@example.test")).FacebookCallback();

        Assert.StartsWith($"{Frontend}/sso-callback?token=token", Assert.IsType<RedirectResult>(result).Url);
    }

    [Fact]
    public async Task AFacebookAccountWithNoEmail_IsSentBackWithAMessage_AndNoAccountIsMade()
    {
        IActionResult result = await Create(Person(email: null)).FacebookCallback();

        Assert.Equal($"{Frontend}/login?error=facebook_no_email", Assert.IsType<RedirectResult>(result).Url);
        _auth.Verify(a => a.ExternalLoginAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ACancelledFacebookSignIn_IsSentBackWithAMessage()
    {
        IActionResult result = await Create(external: null).FacebookCallback();

        Assert.Equal($"{Frontend}/login?error=facebook_failed", Assert.IsType<RedirectResult>(result).Url);
    }

    [Fact]
    public async Task GoogleKeepsItsOwnErrors()
    {
        IActionResult result = await Create(Person(email: null)).GoogleCallback();

        Assert.Equal($"{Frontend}/login?error=sso_missing_claims", Assert.IsType<RedirectResult>(result).Url);
    }
}
