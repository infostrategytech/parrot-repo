using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Moq;
using Parrot.Application.Auth;
using Parrot.Application.Logging;
using Parrot.Application.Models;

namespace Test.Parrot.Api.Tests.Services.Auth;

public class PasswordPolicyValidatorTests
{
    private readonly Mock<IPasswordBreachChecker> _breachMock = new();
    private readonly Mock<IAppLogger<PasswordPolicyValidator>> _loggerMock = new();
    private readonly UserManager<ApplicationUser> _manager;

    public PasswordPolicyValidatorTests()
    {
        Mock<IUserStore<ApplicationUser>> store = new();
        _manager = new Mock<UserManager<ApplicationUser>>(
            store.Object, null!, null!, null!, null!, null!, null!, null!, null!).Object;
    }

    private PasswordPolicyValidator CreateSut(bool checkBreached = true) =>
        new(_breachMock.Object, Options.Create(new AuthSettings { CheckBreachedPasswords = checkBreached }), _loggerMock.Object);

    private static ApplicationUser User() => new()
    {
        Email = "olivia@example.com",
        UserName = "olivia@example.com",
        BusinessName = "Oma's Kitchen",
    };

    [Theory]
    [InlineData("correct horse battery staple")]
    [InlineData("three-tables-under-rain")]
    [InlineData("MyCatIsCalledPudding")]
    public async Task Accepts_a_passphrase(string password)
    {
        _breachMock.Setup(b => b.IsBreachedAsync(password, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        IdentityResult result = await CreateSut().ValidateAsync(_manager, User(), password);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Rejects_anything_over_the_maximum_length()
    {
        IdentityResult result = await CreateSut().ValidateAsync(_manager, User(), new string('x', PasswordPolicy.MaximumLength + 1));

        Assert.Equal("PasswordTooLong", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public async Task Rejects_a_password_built_around_the_email_address()
    {
        IdentityResult result = await CreateSut().ValidateAsync(_manager, User(), "Olivia-rocks-2024");

        Assert.Equal("PasswordContainsAccountDetails", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public async Task Rejects_a_password_built_around_the_business_name()
    {
        IdentityResult result = await CreateSut().ValidateAsync(_manager, User(), "oma's kitchen forever");

        Assert.Equal("PasswordContainsAccountDetails", Assert.Single(result.Errors).Code);
    }

    [Theory]
    [InlineData("Password2024!!")]
    [InlineData("welcome12345")]
    [InlineData("passwordpassword")]
    [InlineData("abcabcabcabc")]
    [InlineData("Qwertyuiop123")]
    public async Task Rejects_the_common_shapes_without_reaching_the_lookup(string password)
    {
        IdentityResult result = await CreateSut().ValidateAsync(_manager, User(), password);

        Assert.Equal("PasswordTooCommon", Assert.Single(result.Errors).Code);
        _breachMock.Verify(b => b.IsBreachedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Rejects_a_password_the_lookup_has_seen_in_a_breach()
    {
        _breachMock.Setup(b => b.IsBreachedAsync("summer holiday 2019", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        IdentityResult result = await CreateSut().ValidateAsync(_manager, User(), "summer holiday 2019");

        Assert.Equal("PasswordBreached", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public async Task Lets_the_password_through_when_the_lookup_is_down()
    {
        // The lookup is a courtesy, not a gate.
        _breachMock
            .Setup(b => b.IsBreachedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("no route to host"));

        IdentityResult result = await CreateSut().ValidateAsync(_manager, User(), "a perfectly fine passphrase");

        Assert.True(result.Succeeded);
        _loggerMock.Verify(l => l.LogWarning(It.IsAny<Exception>(), It.IsAny<string>(), It.IsAny<object?[]>()), Times.Once);
    }

    [Fact]
    public async Task Skips_the_lookup_when_it_is_switched_off()
    {
        IdentityResult result = await CreateSut(checkBreached: false).ValidateAsync(_manager, User(), "a perfectly fine passphrase");

        Assert.True(result.Succeeded);
        _breachMock.Verify(b => b.IsBreachedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
