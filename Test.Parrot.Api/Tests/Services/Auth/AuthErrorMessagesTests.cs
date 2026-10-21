using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Moq;
using Parrot.Application.Auth;
using Parrot.Application.DTOs.Auth;
using Parrot.Application.Email;
using Parrot.Application.FeatureFlags;
using Parrot.Application.Logging;
using Parrot.Application.Models;
using Parrot.Application.Utils;
using Parrot.Domain.Exceptions;
using Parrot.Application.Workspaces;
using Test.Parrot.Api.Tests.Services.Workspaces;

namespace Test.Parrot.Api.Tests.Services.Auth;

// The wording a person sees, one test per row of the table the design side
// signed off on. The frontend shows these as they come back, so a reword here
// is a visible change out there.
public class AuthErrorMessagesTests
{
    private readonly Mock<UserManager<ApplicationUser>> _userManagerMock;
    private readonly Mock<IPasswordHasher<ApplicationUser>> _passwordHasherMock;
    private readonly Mock<IJwtHelper> _jwtHelperMock;
    private readonly FakeWorkspaceRepository _workspaces = new();
    private readonly Mock<IEmailService> _emailServiceMock;
    private readonly Mock<IFeatureFlagService> _featureFlagsMock;
    private readonly Mock<IAppLogger<AuthService>> _loggerMock;
    private readonly Mock<IWebHostEnvironment> _envMock;
    private readonly AuthService _sut;

    public AuthErrorMessagesTests()
    {
        Mock<IUserStore<ApplicationUser>> store = new Mock<IUserStore<ApplicationUser>>();
        _userManagerMock = new Mock<UserManager<ApplicationUser>>(
            store.Object, null!, null!, null!, null!, null!, null!, null!, null!);

        _passwordHasherMock = new Mock<IPasswordHasher<ApplicationUser>>();
        _jwtHelperMock = new Mock<IJwtHelper>();
        _emailServiceMock = new Mock<IEmailService>();
        _featureFlagsMock = new Mock<IFeatureFlagService>();
        _loggerMock = new Mock<IAppLogger<AuthService>>();
        _envMock = new Mock<IWebHostEnvironment>();
        _envMock.SetupGet(e => e.EnvironmentName).Returns("Production");

        _featureFlagsMock
            .Setup(f => f.IsEnabledAsync(FeatureFlagKeys.RequireEmailVerification, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _loggerMock
            .Setup(l => l.BeginEntityScope(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Mock.Of<IDisposable>());

        _jwtHelperMock.Setup(j => j.GetDefaultExpiration()).Returns(DateTime.UtcNow.AddHours(1));
        _jwtHelperMock
            .Setup(j => j.GenerateToken(It.IsAny<IEnumerable<System.Security.Claims.Claim>>(), It.IsAny<DateTime?>()))
            .Returns("test-jwt-token");

        _sut = new AuthService(
            _userManagerMock.Object,
            _passwordHasherMock.Object,
            _jwtHelperMock.Object,
            new WorkspaceService(_workspaces, Mock.Of<IAppLogger<WorkspaceService>>()),
            _emailServiceMock.Object,
            _featureFlagsMock.Object,
            Options.Create(new EmailSettings
            {
                SmtpHost = "localhost",
                SmtpPort = 587,
                SmtpUser = "user",
                SmtpPassword = "pass",
                FromAddress = "from@test.com",
                FromName = "Test",
                BaseUrl = "http://localhost:3000",
            }),
            Options.Create(new AuthSettings { CheckBreachedPasswords = false }),
            _loggerMock.Object,
            _envMock.Object);
    }

    // ------------------------------------------------------------- Sign In

    [Fact]
    public async Task SignIn_UnknownIdentifier_ReturnsTheSignedOffWording()
    {
        _userManagerMock.Setup(m => m.FindByEmailAsync(It.IsAny<string>())).ReturnsAsync((ApplicationUser?)null);

        UnauthorizedException ex = await Assert.ThrowsAsync<UnauthorizedException>(() =>
            _sut.LoginAsync(new LoginRequest { Identifier = "nobody@test.com", Password = "pass" }));

        Assert.Equal(
            "Unable to sign you in. Please check your credentials for errors and try again.",
            ex.Message);
    }

    [Fact]
    public async Task SignIn_LockedAccount_KeepsTheLockoutWording()
    {
        ApplicationUser user = CreateUser();
        _userManagerMock.Setup(m => m.FindByEmailAsync(user.Email!)).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.IsLockedOutAsync(user)).ReturnsAsync(true);

        UnauthorizedException ex = await Assert.ThrowsAsync<UnauthorizedException>(() =>
            _sut.LoginAsync(new LoginRequest { Identifier = user.Email!, Password = "any" }));

        Assert.Contains("locked", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AuthMessages.AccountLocked, ex.Message);
    }

    // ------------------------------------------------------------- Sign Up

    [Fact]
    public async Task SignUp_DuplicateAccount_ReturnsTheSignedOffWording()
    {
        ApplicationUser existing = CreateUser(emailConfirmed: true);
        _userManagerMock.Setup(m => m.FindByEmailAsync(existing.Email!)).ReturnsAsync(existing);

        ConflictException ex = await Assert.ThrowsAsync<ConflictException>(() =>
            _sut.RegisterAsync(new RegisterRequest
            {
                Email = existing.Email!,
                Password = "Pass123!",
                ConfirmPassword = "Pass123!",
            }));

        Assert.Equal("An account already exists with these details.", ex.Message);
        Assert.Equal("CONFLICT", ex.ErrorCode);
    }

    [Fact]
    public async Task SignUp_ShortPassword_IsRefusedWithTheEightCharacterWording()
    {
        // Registration collects nothing but an address and a password, and the
        // floor is eight characters.
        _userManagerMock.Setup(m => m.FindByEmailAsync("new@test.com")).ReturnsAsync((ApplicationUser?)null);
        _userManagerMock
            .Setup(m => m.CreateAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()))
            .ReturnsAsync(IdentityResult.Failed(new IdentityError
            {
                Code = "PasswordTooShort",
                Description = "Passwords must be at least 8 characters.",
            }));

        ValidationException ex = await Assert.ThrowsAsync<ValidationException>(() =>
            _sut.RegisterAsync(new RegisterRequest
            {
                Email = "new@test.com",
                Password = "short1!",
                ConfirmPassword = "short1!",
            }));

        Assert.Contains("at least 8 characters", string.Join(" ", ex.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void SignUp_CollectsOnlyAnAddressAndPassword()
    {
        // The signup payload has no business name: that arrives with the setup
        // step after sign-in. This pins the contract so it cannot creep back.
        string[] properties = typeof(RegisterRequest)
            .GetProperties()
            .Select(p => p.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "ConfirmPassword", "Email", "Password" }, properties);
    }

    [Fact]
    public void PasswordFloor_IsEightCharactersInEverySharedPlace()
    {
        Assert.Equal(8, PasswordPolicy.MinimumLength);
        Assert.Equal(8, new AuthSettings().MinimumPasswordLength);
        Assert.Equal(128, PasswordPolicy.MaximumLength);
    }

    // ------------------------------------------------------- Reset Password

    [Fact]
    public async Task ResetPassword_ExpiredLink_ReturnsTheSignedOffWording()
    {
        ApplicationUser user = CreateUser();
        _userManagerMock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        _userManagerMock
            .Setup(m => m.ResetPasswordAsync(user, "tok", It.IsAny<string>()))
            .ReturnsAsync(IdentityResult.Failed(
                new IdentityError { Code = nameof(IdentityErrorDescriber.InvalidToken), Description = "Invalid token." }));

        ValidationException ex = await Assert.ThrowsAsync<ValidationException>(() =>
            _sut.ResetPasswordAsync(new ResetPasswordRequest
            {
                UserId = user.Id,
                Token = "tok",
                NewPassword = "NewPass1!",
                ConfirmNewPassword = "NewPass1!",
            }));

        Assert.Equal("This reset link has expired.", ex.Message);
    }

    [Fact]
    public async Task ResetPassword_UnknownAccount_ReturnsTheSignedOffWording()
    {
        _userManagerMock.Setup(m => m.FindByIdAsync("missing")).ReturnsAsync((ApplicationUser?)null);

        NotFoundException ex = await Assert.ThrowsAsync<NotFoundException>(() =>
            _sut.ResetPasswordAsync(new ResetPasswordRequest
            {
                UserId = "missing",
                Token = "tok",
                NewPassword = "NewPass1!",
                ConfirmNewPassword = "NewPass1!",
            }));

        Assert.Equal("We could not find the account this link is for.", ex.Message);
        Assert.Equal("NOT_FOUND", ex.ErrorCode);
    }

    // --------------------------------------------------------- Verify Email

    [Fact]
    public async Task VerifyEmail_WrongCode_KeepsWordingThatMentionsExpiry()
    {
        // The frontend locks the boxes when it sees "expired", so the message
        // has to keep saying it.
        ApplicationUser user = CreateUser(emailConfirmed: false);
        _userManagerMock.Setup(m => m.FindByEmailAsync(user.Email!)).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.IsLockedOutAsync(user)).ReturnsAsync(false);
        _userManagerMock
            .Setup(m => m.ConfirmEmailAsync(user, "000000"))
            .ReturnsAsync(IdentityResult.Failed(new IdentityError { Description = "Invalid token" }));
        _userManagerMock.Setup(m => m.AccessFailedAsync(user)).ReturnsAsync(IdentityResult.Success);

        ValidationException ex = await Assert.ThrowsAsync<ValidationException>(() =>
            _sut.VerifyEmailAsync(new VerifyEmailRequest { Email = user.Email!, Code = "000000" }));

        Assert.Contains("expired", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AuthMessages.InvalidCode, ex.Message);
    }

    // A setup request can be turned away by the request annotations before the
    // service sees it, and a person must not be able to tell the two paths
    // apart — so both have to carry the very same words.
    [Theory]
    [InlineData(nameof(UpdateProfileRequest.BusinessName), nameof(AuthMessages.BusinessNameTooLong))]
    [InlineData(nameof(UpdateProfileRequest.CountryCode), nameof(AuthMessages.InvalidDialCode))]
    [InlineData(nameof(UpdateProfileRequest.Number), nameof(AuthMessages.InvalidPhoneNumber))]
    public void ProfileRequest_AnnotationWording_IsTheWordingTheServiceUses(string property, string constant)
    {
        var attribute = typeof(UpdateProfileRequest)
            .GetProperty(property)!
            .GetCustomAttributes(typeof(System.ComponentModel.DataAnnotations.StringLengthAttribute), inherit: false)
            .OfType<System.ComponentModel.DataAnnotations.StringLengthAttribute>()
            .Single();

        string signedOff = (string)typeof(AuthMessages).GetField(constant)!.GetRawConstantValue()!;

        Assert.Equal(signedOff, attribute.ErrorMessage);
    }

    private static ApplicationUser CreateUser(bool emailConfirmed = false) =>
        new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            Email = "user@test.com",
            UserName = "user@test.com",
            EmailConfirmed = emailConfirmed,
        };
}
