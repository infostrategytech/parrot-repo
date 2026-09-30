using System.Security.Claims;
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

namespace Test.Parrot.Api.Tests.Services.Auth;

public class AuthServiceTests
{
    private readonly Mock<UserManager<ApplicationUser>> _userManagerMock;
    private readonly Mock<IPasswordHasher<ApplicationUser>> _passwordHasherMock;
    private readonly Mock<IJwtHelper> _jwtHelperMock;
    private readonly Mock<IEmailService> _emailServiceMock;
    private readonly Mock<IFeatureFlagService> _featureFlagsMock;
    private readonly Mock<IAppLogger<AuthService>> _loggerMock;
    private readonly Mock<IWebHostEnvironment> _envMock;
    private readonly AuthService _sut;

    public AuthServiceTests()
    {
        Mock<IUserStore<ApplicationUser>> store = new Mock<IUserStore<ApplicationUser>>();
        _userManagerMock = new Mock<UserManager<ApplicationUser>>(
            store.Object, null!, null!, null!, null!, null!, null!, null!, null!);

        _passwordHasherMock = new Mock<IPasswordHasher<ApplicationUser>>();
        _jwtHelperMock = new Mock<IJwtHelper>();
        _emailServiceMock = new Mock<IEmailService>();
        _featureFlagsMock = new Mock<IFeatureFlagService>();
        RequireVerification(true);
        _loggerMock = new Mock<IAppLogger<AuthService>>();
        _envMock = new Mock<IWebHostEnvironment>();
        _envMock.SetupGet(e => e.EnvironmentName).Returns("Production");

        _loggerMock
            .Setup(l => l.BeginEntityScope(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Mock.Of<IDisposable>());

        _jwtHelperMock
            .Setup(j => j.GetDefaultExpiration())
            .Returns(DateTime.UtcNow.AddHours(1));

        _jwtHelperMock
            .Setup(j => j.GenerateToken(It.IsAny<IEnumerable<Claim>>(), It.IsAny<DateTime?>()))
            .Returns("test-jwt-token");

        _sut = CreateSut(EmailOptions());
    }

    private void RequireVerification(bool required) =>
        _featureFlagsMock
            .Setup(f => f.IsEnabledAsync(FeatureFlagKeys.RequireEmailVerification, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(required);

    private static IOptions<EmailSettings> EmailOptions(bool sendInDevelopment = false) =>
        Options.Create(new EmailSettings
        {
            SmtpHost = "localhost",
            SmtpPort = 587,
            SmtpUser = "user",
            SmtpPassword = "pass",
            FromAddress = "from@test.com",
            FromName = "Test",
            BaseUrl = "http://localhost:3000",
            SendInDevelopment = sendInDevelopment,
        });

    // The fixture carries the development code so tests can show it is honoured
    // only when the environment says Development as well.
    private AuthService CreateSut(IOptions<EmailSettings> emailSettings) =>
        new AuthService(
            _userManagerMock.Object,
            _passwordHasherMock.Object,
            _jwtHelperMock.Object,
            _emailServiceMock.Object,
            _featureFlagsMock.Object,
            emailSettings,
            Options.Create(new AuthSettings { DevelopmentVerificationCode = "222222", CheckBreachedPasswords = false }),
            _loggerMock.Object,
            _envMock.Object);

    // ---------------------------------------------------------------------------
    // LoginAsync
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task LoginAsync_WhenAccountIsLockedOut_ThrowsUnauthorizedException()
    {
        ApplicationUser user = CreateUser(emailConfirmed: true);
        _userManagerMock.Setup(m => m.FindByEmailAsync(user.Email!)).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.IsLockedOutAsync(user)).ReturnsAsync(true);

        UnauthorizedException ex = await Assert.ThrowsAsync<UnauthorizedException>(() =>
            _sut.LoginAsync(new LoginRequest { Identifier = user.Email!, Password = "any" }));

        Assert.Contains("locked", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoginAsync_WhenUserNotFound_ThrowsUnauthorizedException()
    {
        _userManagerMock
            .Setup(m => m.FindByEmailAsync(It.IsAny<string>()))
            .ReturnsAsync((ApplicationUser?)null);

        await Assert.ThrowsAsync<UnauthorizedException>(() =>
            _sut.LoginAsync(new LoginRequest { Identifier = "nobody@test.com", Password = "pass" }));
    }

    [Fact]
    public async Task LoginAsync_WhenUserNotFound_DoesNotCallAccessFailed()
    {
        _userManagerMock
            .Setup(m => m.FindByEmailAsync(It.IsAny<string>()))
            .ReturnsAsync((ApplicationUser?)null);

        await Assert.ThrowsAsync<UnauthorizedException>(() =>
            _sut.LoginAsync(new LoginRequest { Identifier = "nobody@test.com", Password = "pass" }));

        _userManagerMock.Verify(m => m.AccessFailedAsync(It.IsAny<ApplicationUser>()), Times.Never);
    }

    [Fact]
    public async Task LoginAsync_WhenPasswordInvalid_ThrowsUnauthorizedException_AndIncrementsFailedCount()
    {
        ApplicationUser user = CreateUser(emailConfirmed: true);
        _userManagerMock.Setup(m => m.FindByEmailAsync(user.Email!)).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.IsLockedOutAsync(user)).ReturnsAsync(false);
        _userManagerMock.Setup(m => m.CheckPasswordAsync(user, "wrong")).ReturnsAsync(false);
        _userManagerMock.Setup(m => m.AccessFailedAsync(user)).ReturnsAsync(IdentityResult.Success);

        await Assert.ThrowsAsync<UnauthorizedException>(() =>
            _sut.LoginAsync(new LoginRequest { Identifier = user.Email!, Password = "wrong" }));

        _userManagerMock.Verify(m => m.AccessFailedAsync(user), Times.Once);
    }

    [Fact]
    public async Task LoginAsync_WhenEmailNotConfirmed_ThrowsEmailNotVerified_AndSendsAFreshCode()
    {
        ApplicationUser user = CreateUser(emailConfirmed: false);
        _userManagerMock.Setup(m => m.FindByEmailAsync(user.Email!)).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.IsLockedOutAsync(user)).ReturnsAsync(false);
        _userManagerMock.Setup(m => m.CheckPasswordAsync(user, "correct")).ReturnsAsync(true);
        _userManagerMock.Setup(m => m.GenerateEmailConfirmationTokenAsync(user)).ReturnsAsync("123456");

        EmailNotVerifiedException ex = await Assert.ThrowsAsync<EmailNotVerifiedException>(() =>
            _sut.LoginAsync(new LoginRequest { Identifier = user.Email!, Password = "correct" }));

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, ex.StatusCode);
        _emailServiceMock.Verify(
            e => e.SendEmailAsync(user.Email!, It.IsAny<string>(), It.Is<string>(body => body.Contains("123456"))),
            Times.Once);
    }

    [Fact]
    public async Task LoginAsync_WhenEmailNotConfirmed_ButPasswordWrong_GivesTheGenericAnswer()
    {
        // Verification state must not leak to someone who does not hold the password.
        ApplicationUser user = CreateUser(emailConfirmed: false);
        _userManagerMock.Setup(m => m.FindByEmailAsync(user.Email!)).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.IsLockedOutAsync(user)).ReturnsAsync(false);
        _userManagerMock.Setup(m => m.CheckPasswordAsync(user, "wrong")).ReturnsAsync(false);
        _userManagerMock.Setup(m => m.AccessFailedAsync(user)).ReturnsAsync(IdentityResult.Success);

        await Assert.ThrowsAsync<UnauthorizedException>(() =>
            _sut.LoginAsync(new LoginRequest { Identifier = user.Email!, Password = "wrong" }));

        _emailServiceMock.Verify(
            e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task LoginAsync_WhenUserNotFound_StillRunsAPasswordHash()
    {
        // So an unknown address takes as long to answer as a wrong password.
        _userManagerMock
            .Setup(m => m.FindByEmailAsync(It.IsAny<string>()))
            .ReturnsAsync((ApplicationUser?)null);

        await Assert.ThrowsAsync<UnauthorizedException>(() =>
            _sut.LoginAsync(new LoginRequest { Identifier = "nobody@test.com", Password = "pass" }));

        _passwordHasherMock.Verify(h => h.HashPassword(It.IsAny<ApplicationUser>(), "pass"), Times.Once);
    }

    [Fact]
    public async Task LoginAsync_WhenCredentialsValid_ReturnsAuthResponse_AndResetsAccessFailed()
    {
        ApplicationUser user = CreateUser(emailConfirmed: true);
        _userManagerMock.Setup(m => m.FindByEmailAsync(user.Email!)).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.IsLockedOutAsync(user)).ReturnsAsync(false);
        _userManagerMock.Setup(m => m.CheckPasswordAsync(user, "correct")).ReturnsAsync(true);
        _userManagerMock.Setup(m => m.ResetAccessFailedCountAsync(user)).ReturnsAsync(IdentityResult.Success);

        AuthResponse response = await _sut.LoginAsync(
            new LoginRequest { Identifier = user.Email!, Password = "correct" });

        Assert.Equal("test-jwt-token", response.Token);
        Assert.Equal(user.Id, response.UserId);
        Assert.Equal(user.Email, response.Email);
        Assert.Equal(user.BusinessName, response.BusinessName);
        _userManagerMock.Verify(m => m.ResetAccessFailedCountAsync(user), Times.Once);
    }

    [Fact]
    public async Task LoginAsync_CarriesTheProfileFieldsTheSetupStepFills()
    {
        // The response is what the frontend decides "still needs setup" from,
        // so an account that has been through setup must show it, and one that
        // has not must show nulls rather than empty strings.
        ApplicationUser unset = CreateUser(emailConfirmed: true);
        unset.BusinessName = null;
        unset.LogoUrl = null;
        unset.PhoneDialCode = null;
        unset.PhoneNumber = null;
        _userManagerMock.Setup(m => m.FindByEmailAsync(unset.Email!)).ReturnsAsync(unset);
        _userManagerMock.Setup(m => m.IsLockedOutAsync(unset)).ReturnsAsync(false);
        _userManagerMock.Setup(m => m.CheckPasswordAsync(unset, "correct")).ReturnsAsync(true);
        _userManagerMock.Setup(m => m.ResetAccessFailedCountAsync(unset)).ReturnsAsync(IdentityResult.Success);

        AuthResponse fresh = await _sut.LoginAsync(
            new LoginRequest { Identifier = unset.Email!, Password = "correct" });

        Assert.Null(fresh.BusinessName);
        Assert.Null(fresh.LogoUrl);
        Assert.Null(fresh.PhoneNumber);

        ApplicationUser set = CreatePhoneUser(emailConfirmed: true);
        set.BusinessName = "Oma's Kitchen";
        set.LogoUrl = "https://cdn.example.com/logo.png";
        _userManagerMock.Setup(m => m.FindByEmailAsync(set.Email!)).ReturnsAsync(set);
        _userManagerMock.Setup(m => m.IsLockedOutAsync(set)).ReturnsAsync(false);
        _userManagerMock.Setup(m => m.CheckPasswordAsync(set, "correct")).ReturnsAsync(true);
        _userManagerMock.Setup(m => m.ResetAccessFailedCountAsync(set)).ReturnsAsync(IdentityResult.Success);

        AuthResponse done = await _sut.LoginAsync(
            new LoginRequest { Identifier = set.Email!, Password = "correct" });

        Assert.Equal("Oma's Kitchen", done.BusinessName);
        Assert.Equal("https://cdn.example.com/logo.png", done.LogoUrl);
        Assert.Equal("+2348012345678", done.PhoneNumber);
    }

    [Fact]
    public async Task LoginAsync_WhenLockedOut_DoesNotCheckPassword()
    {
        ApplicationUser user = CreateUser(emailConfirmed: true);
        _userManagerMock.Setup(m => m.FindByEmailAsync(user.Email!)).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.IsLockedOutAsync(user)).ReturnsAsync(true);

        await Assert.ThrowsAsync<UnauthorizedException>(() =>
            _sut.LoginAsync(new LoginRequest { Identifier = user.Email!, Password = "any" }));

        _userManagerMock.Verify(m => m.CheckPasswordAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()), Times.Never);
        _userManagerMock.Verify(m => m.AccessFailedAsync(It.IsAny<ApplicationUser>()), Times.Never);
    }

    [Fact]
    public async Task LoginAsync_WhenEmailNotConfirmed_DoesNotIncrementOrResetAccessFailed()
    {
        ApplicationUser user = CreateUser(emailConfirmed: false);
        _userManagerMock.Setup(m => m.FindByEmailAsync(user.Email!)).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.IsLockedOutAsync(user)).ReturnsAsync(false);
        _userManagerMock.Setup(m => m.CheckPasswordAsync(user, "correct")).ReturnsAsync(true);

        await Assert.ThrowsAsync<EmailNotVerifiedException>(() =>
            _sut.LoginAsync(new LoginRequest { Identifier = user.Email!, Password = "correct" }));

        _userManagerMock.Verify(m => m.AccessFailedAsync(It.IsAny<ApplicationUser>()), Times.Never);
        _userManagerMock.Verify(m => m.ResetAccessFailedCountAsync(It.IsAny<ApplicationUser>()), Times.Never);
    }

    // ---------------------------------------------------------------------------
    // Login by phone
    // ---------------------------------------------------------------------------

    // A sign-in form takes one detail, so every way of typing the same number
    // has to reach the same row the account is stored under.
    [Theory]
    [InlineData("08012345678")]
    [InlineData("2348012345678")]
    [InlineData("+2348012345678")]
    public async Task LoginAsync_WhenIdentifierIsAPhone_FindsTheAccountWhateverTheFormat(string typed)
    {
        ApplicationUser user = CreatePhoneUser(emailConfirmed: true);
        UsingPhoneBook(user);

        _userManagerMock.Setup(m => m.IsLockedOutAsync(user)).ReturnsAsync(false);
        _userManagerMock.Setup(m => m.CheckPasswordAsync(user, "correct")).ReturnsAsync(true);
        _userManagerMock.Setup(m => m.ResetAccessFailedCountAsync(user)).ReturnsAsync(IdentityResult.Success);

        AuthResponse response = await _sut.LoginAsync(
            new LoginRequest { Identifier = typed, Password = "correct" });

        Assert.Equal(user.Id, response.UserId);
        Assert.Equal("+2348012345678", response.PhoneNumber);
        _userManagerMock.Verify(m => m.FindByEmailAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task LoginAsync_WhenPhoneIsUnknown_UsesTheSameGenericMessageAsABadPassword()
    {
        // Neither the shape of the identifier nor the wording may say which of
        // the two it was, or the endpoint becomes a way to find out who exists.
        UsingPhoneBook();

        UnauthorizedException unknown = await Assert.ThrowsAsync<UnauthorizedException>(() =>
            _sut.LoginAsync(new LoginRequest { Identifier = "08000000000", Password = "pass" }));

        Assert.Equal(AuthMessages.InvalidCredentials, unknown.Message);
        _passwordHasherMock.Verify(h => h.HashPassword(It.IsAny<ApplicationUser>(), "pass"), Times.Once);
    }

    [Fact]
    public async Task LoginAsync_WhenPasswordIsWrong_UsesTheSameMessageAsAnUnknownPhone()
    {
        ApplicationUser user = CreatePhoneUser(emailConfirmed: true);
        UsingPhoneBook(user);

        _userManagerMock.Setup(m => m.IsLockedOutAsync(user)).ReturnsAsync(false);
        _userManagerMock.Setup(m => m.CheckPasswordAsync(user, "wrong")).ReturnsAsync(false);
        _userManagerMock.Setup(m => m.AccessFailedAsync(user)).ReturnsAsync(IdentityResult.Success);

        UnauthorizedException wrong = await Assert.ThrowsAsync<UnauthorizedException>(() =>
            _sut.LoginAsync(new LoginRequest { Identifier = "+2348012345678", Password = "wrong" }));

        Assert.Equal(AuthMessages.InvalidCredentials, wrong.Message);
        _userManagerMock.Verify(m => m.AccessFailedAsync(user), Times.Once);
    }

    [Fact]
    public async Task LoginAsync_WhenIdentifierIsANeitherAddressNorPhone_GetsTheGenericRefusal()
    {
        UsingPhoneBook();

        UnauthorizedException ex = await Assert.ThrowsAsync<UnauthorizedException>(() =>
            _sut.LoginAsync(new LoginRequest { Identifier = "just some words", Password = "pass" }));

        Assert.Equal(AuthMessages.InvalidCredentials, ex.Message);
    }

    [Fact]
    public async Task LoginAsync_WhenPhoneAccountIsLockedOut_ShowsTheLockoutMessage()
    {
        ApplicationUser user = CreatePhoneUser(emailConfirmed: true);
        UsingPhoneBook(user);

        _userManagerMock.Setup(m => m.IsLockedOutAsync(user)).ReturnsAsync(true);

        UnauthorizedException ex = await Assert.ThrowsAsync<UnauthorizedException>(() =>
            _sut.LoginAsync(new LoginRequest { Identifier = "08012345678", Password = "any" }));

        Assert.Contains("locked", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // Puts a small phone book behind UserManager.Users, the way sign-in reads it.
    private void UsingPhoneBook(params ApplicationUser[] users) =>
        _userManagerMock.SetupGet(m => m.Users).Returns(users.AsQueryable());

    private static ApplicationUser CreatePhoneUser(bool emailConfirmed = false) =>
        new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            Email = "phoneowner@test.com",
            UserName = "phoneowner@test.com",
            BusinessName = "Test Business",
            EmailConfirmed = emailConfirmed,
            PhoneDialCode = "234",
            PhoneNumber = "8012345678",
        };

    // ---------------------------------------------------------------------------
    // RegisterAsync
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task RegisterAsync_WhenEmailAlreadyRegistered_ThrowsConflictException()
    {
        ApplicationUser existing = CreateUser(emailConfirmed: true);
        _userManagerMock.Setup(m => m.FindByEmailAsync(existing.Email!)).ReturnsAsync(existing);

        await Assert.ThrowsAsync<ConflictException>(() =>
            _sut.RegisterAsync(new RegisterRequest
            {
                Email = existing.Email!,
                Password = "Pass123!",
                ConfirmPassword = "Pass123!",
            }));
    }

    [Fact]
    public async Task RegisterAsync_WhenEmailAlreadyRegistered_DoesNotSendVerificationEmail()
    {
        ApplicationUser existing = CreateUser(emailConfirmed: true);
        _userManagerMock.Setup(m => m.FindByEmailAsync(existing.Email!)).ReturnsAsync(existing);

        await Assert.ThrowsAsync<ConflictException>(() =>
            _sut.RegisterAsync(new RegisterRequest
            {
                Email = existing.Email!,
                Password = "Pass123!",
                ConfirmPassword = "Pass123!",
            }));

        _emailServiceMock.Verify(
            e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task RegisterAsync_WhenAccountExistsButIsUnverified_ResendsVerificationInsteadOfConflicting()
    {
        ApplicationUser existing = CreateUser();
        _userManagerMock.Setup(m => m.FindByEmailAsync(existing.Email!)).ReturnsAsync(existing);
        _userManagerMock
            .Setup(m => m.GenerateEmailConfirmationTokenAsync(existing))
            .ReturnsAsync("confirm-token");

        await _sut.RegisterAsync(new RegisterRequest
        {
            Email = existing.Email!,
            Password = "Pass123!",
            ConfirmPassword = "Pass123!",
        });

        _emailServiceMock.Verify(
            e => e.SendEmailAsync(existing.Email!, It.IsAny<string>(), It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public async Task RegisterAsync_WhenAccountExistsButIsUnverified_LeavesThePasswordAlone()
    {
        ApplicationUser existing = CreateUser();
        _userManagerMock.Setup(m => m.FindByEmailAsync(existing.Email!)).ReturnsAsync(existing);
        _userManagerMock
            .Setup(m => m.GenerateEmailConfirmationTokenAsync(existing))
            .ReturnsAsync("confirm-token");

        await _sut.RegisterAsync(new RegisterRequest
        {
            Email = existing.Email!,
            Password = "Different123!",
            ConfirmPassword = "Different123!",
        });

        _userManagerMock.Verify(
            m => m.CreateAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()),
            Times.Never);
        _userManagerMock.Verify(
            m => m.RemovePasswordAsync(It.IsAny<ApplicationUser>()),
            Times.Never);
    }

    [Fact]
    public async Task RegisterAsync_WhenVerificationEmailFails_StillReportsSuccess()
    {
        // The account is committed before the email goes out, so a failed send
        // must not be reported as a failed sign-up: the caller would retry and
        // hit a conflict on an address they cannot sign in with either.
        _userManagerMock
            .Setup(m => m.FindByEmailAsync("new@test.com"))
            .ReturnsAsync((ApplicationUser?)null);
        _userManagerMock
            .Setup(m => m.CreateAsync(It.IsAny<ApplicationUser>(), "Pass123!"))
            .ReturnsAsync(IdentityResult.Success);
        _userManagerMock
            .Setup(m => m.GenerateEmailConfirmationTokenAsync(It.IsAny<ApplicationUser>()))
            .ReturnsAsync("confirm-token");
        _emailServiceMock
            .Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("smtp is down"));

        await _sut.RegisterAsync(new RegisterRequest
        {
            Email = "new@test.com",
            Password = "Pass123!",
            ConfirmPassword = "Pass123!",
        });

        _userManagerMock.Verify(
            m => m.CreateAsync(It.IsAny<ApplicationUser>(), "Pass123!"),
            Times.Once);
    }

    [Fact]
    public async Task ResendVerificationEmailAsync_WhenEmailSendFails_DoesNotThrow()
    {
        // Failing only for addresses that exist would answer the enumeration
        // question the unknown-address path stays silent to avoid.
        ApplicationUser existing = CreateUser();
        _userManagerMock.Setup(m => m.FindByEmailAsync(existing.Email!)).ReturnsAsync(existing);
        _userManagerMock
            .Setup(m => m.GenerateEmailConfirmationTokenAsync(existing))
            .ReturnsAsync("confirm-token");
        _emailServiceMock
            .Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("smtp is down"));

        await _sut.ResendVerificationEmailAsync(new ResendVerificationEmailRequest
        {
            Email = existing.Email!,
        });
    }

    [Fact]
    public async Task RegisterAsync_WhenIdentityRejectsThePassword_SurfacesItsWording()
    {
        _userManagerMock
            .Setup(m => m.FindByEmailAsync(It.IsAny<string>()))
            .ReturnsAsync((ApplicationUser?)null);
        _userManagerMock
            .Setup(m => m.CreateAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()))
            .ReturnsAsync(IdentityResult.Failed(new IdentityError
            {
                Code = "PasswordBreached",
                Description = "That password has appeared in a data breach. Choose a different one.",
            }));

        ValidationException ex = await Assert.ThrowsAsync<ValidationException>(() =>
            _sut.RegisterAsync(new RegisterRequest
            {
                Email = "new@test.com",
                Password = "password2024!!",
                ConfirmPassword = "password2024!!",
            }));

        Assert.Contains("data breach", string.Join(" ", ex.Errors ?? []));
    }

    [Fact]
    public async Task RegisterAsync_EmailCarriesTheSixDigitCode_NotALink()
    {
        _userManagerMock
            .Setup(m => m.FindByEmailAsync("new@test.com"))
            .ReturnsAsync((ApplicationUser?)null);
        _userManagerMock
            .Setup(m => m.CreateAsync(It.IsAny<ApplicationUser>(), "Pass123!"))
            .ReturnsAsync(IdentityResult.Success);
        _userManagerMock
            .Setup(m => m.GenerateEmailConfirmationTokenAsync(It.IsAny<ApplicationUser>()))
            .ReturnsAsync("482913");

        await _sut.RegisterAsync(new RegisterRequest
        {
            Email = "new@test.com",
            Password = "Pass123!",
            ConfirmPassword = "Pass123!",
        });

        _emailServiceMock.Verify(
            e => e.SendEmailAsync(
                "new@test.com",
                It.IsAny<string>(),
                It.Is<string>(body => body.Contains("482913") && !body.Contains("token="))),
            Times.Once);
    }

    [Fact]
    public async Task RegisterAsync_InDevelopment_EmailCarriesTheConfiguredCode()
    {
        // What a local mailbox shows must be what the verify screen accepts.
        _envMock.SetupGet(e => e.EnvironmentName).Returns("Development");
        AuthService sut = CreateSut(EmailOptions(sendInDevelopment: true));
        _userManagerMock
            .Setup(m => m.FindByEmailAsync("new@test.com"))
            .ReturnsAsync((ApplicationUser?)null);
        _userManagerMock
            .Setup(m => m.CreateAsync(It.IsAny<ApplicationUser>(), "Pass123!"))
            .ReturnsAsync(IdentityResult.Success);

        await sut.RegisterAsync(new RegisterRequest
        {
            Email = "new@test.com",
            Password = "Pass123!",
            ConfirmPassword = "Pass123!",
        });

        _emailServiceMock.Verify(
            e => e.SendEmailAsync("new@test.com", It.IsAny<string>(), It.Is<string>(body => body.Contains("222222"))),
            Times.Once);
        _userManagerMock.Verify(m => m.GenerateEmailConfirmationTokenAsync(It.IsAny<ApplicationUser>()), Times.Never);
    }

    [Fact]
    public async Task RegisterAsync_WhenVerificationRequired_HandsBackNoSession()
    {
        _userManagerMock
            .Setup(m => m.FindByEmailAsync("new@test.com"))
            .ReturnsAsync((ApplicationUser?)null);
        _userManagerMock
            .Setup(m => m.CreateAsync(It.IsAny<ApplicationUser>(), "Pass123!"))
            .ReturnsAsync(IdentityResult.Success);
        _userManagerMock
            .Setup(m => m.GenerateEmailConfirmationTokenAsync(It.IsAny<ApplicationUser>()))
            .ReturnsAsync("123456");

        AuthResponse? response = await _sut.RegisterAsync(new RegisterRequest
        {
            Email = "new@test.com",
            Password = "Pass123!",
            ConfirmPassword = "Pass123!",
        });

        Assert.Null(response);
    }

    [Fact]
    public async Task RegisterAsync_WhenVerificationSwitchedOff_CreatesAConfirmedAccount_AndSignsIn()
    {
        RequireVerification(false);
        ApplicationUser? created = null;
        _userManagerMock
            .Setup(m => m.FindByEmailAsync("new@test.com"))
            .ReturnsAsync((ApplicationUser?)null);
        _userManagerMock
            .Setup(m => m.CreateAsync(It.IsAny<ApplicationUser>(), "Pass123!"))
            .Callback<ApplicationUser, string>((u, _) => created = u)
            .ReturnsAsync(IdentityResult.Success);

        AuthResponse? response = await _sut.RegisterAsync(new RegisterRequest
        {
            Email = "new@test.com",
            Password = "Pass123!",
            ConfirmPassword = "Pass123!",
        });

        Assert.NotNull(response);
        Assert.Equal("test-jwt-token", response.Token);
        Assert.NotNull(created);
        Assert.True(created.EmailConfirmed);
        _emailServiceMock.Verify(
            e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task RegisterAsync_WhenVerificationSwitchedOff_AnUnverifiedExistingAccountConflicts()
    {
        // It can sign in now, so a retry is a duplicate rather than a resend.
        RequireVerification(false);
        ApplicationUser existing = CreateUser();
        _userManagerMock.Setup(m => m.FindByEmailAsync(existing.Email!)).ReturnsAsync(existing);

        await Assert.ThrowsAsync<ConflictException>(() =>
            _sut.RegisterAsync(new RegisterRequest
            {
                Email = existing.Email!,
                Password = "Pass123!",
                ConfirmPassword = "Pass123!",
            }));

        _emailServiceMock.Verify(
            e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task LoginAsync_WhenVerificationSwitchedOff_LetsAnUnverifiedAccountIn_AndConfirmsIt()
    {
        RequireVerification(false);
        ApplicationUser user = CreateUser(emailConfirmed: false);
        _userManagerMock.Setup(m => m.FindByEmailAsync(user.Email!)).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.IsLockedOutAsync(user)).ReturnsAsync(false);
        _userManagerMock.Setup(m => m.CheckPasswordAsync(user, "correct")).ReturnsAsync(true);
        _userManagerMock.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);
        _userManagerMock.Setup(m => m.ResetAccessFailedCountAsync(user)).ReturnsAsync(IdentityResult.Success);

        AuthResponse response = await _sut.LoginAsync(new LoginRequest { Identifier = user.Email!, Password = "correct" });

        Assert.Equal("test-jwt-token", response.Token);
        Assert.True(user.EmailConfirmed);
        _userManagerMock.Verify(m => m.UpdateAsync(user), Times.Once);
        _emailServiceMock.Verify(
            e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task RegisterAsync_WhenIdentityCreationFails_ThrowsValidationException()
    {
        _userManagerMock
            .Setup(m => m.FindByEmailAsync(It.IsAny<string>()))
            .ReturnsAsync((ApplicationUser?)null);
        _userManagerMock
            .Setup(m => m.CreateAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()))
            .ReturnsAsync(IdentityResult.Failed(new IdentityError { Description = "Error" }));

        await Assert.ThrowsAsync<ValidationException>(() =>
            _sut.RegisterAsync(new RegisterRequest
            {
                Email = "new@test.com",
                Password = "Pass123!",
                ConfirmPassword = "Pass123!",
            }));

        _emailServiceMock.Verify(
            e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task RegisterAsync_WhenSuccessful_SendsVerificationEmail_AndReturnsNoToken()
    {
        _userManagerMock
            .Setup(m => m.FindByEmailAsync("new@test.com"))
            .ReturnsAsync((ApplicationUser?)null);
        _userManagerMock
            .Setup(m => m.CreateAsync(It.IsAny<ApplicationUser>(), "Pass123!"))
            .ReturnsAsync(IdentityResult.Success);
        _userManagerMock
            .Setup(m => m.GenerateEmailConfirmationTokenAsync(It.IsAny<ApplicationUser>()))
            .ReturnsAsync("confirm-token");
        _emailServiceMock
            .Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        await _sut.RegisterAsync(new RegisterRequest
        {
            Email = "new@test.com",
            Password = "Pass123!",
            ConfirmPassword = "Pass123!",
        });

        _emailServiceMock.Verify(
            e => e.SendEmailAsync(
                "new@test.com",
                It.IsAny<string>(),
                It.Is<string>(body => body.Contains("http://localhost:3000"))),
            Times.Once);

        _jwtHelperMock.Verify(
            j => j.GenerateToken(It.IsAny<IEnumerable<Claim>>(), It.IsAny<DateTime?>()),
            Times.Never);
    }

    // ---------------------------------------------------------------------------
    // VerifyEmailAsync
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task VerifyEmailAsync_WhenEmailUnknown_AnswersLikeAWrongCode()
    {
        // A 404 here would say which addresses have accounts.
        _userManagerMock
            .Setup(m => m.FindByEmailAsync("nobody@test.com"))
            .ReturnsAsync((ApplicationUser?)null);

        await Assert.ThrowsAsync<ValidationException>(() =>
            _sut.VerifyEmailAsync(new VerifyEmailRequest { Email = "nobody@test.com", Code = "123456" }));
    }

    [Fact]
    public async Task VerifyEmailAsync_WhenCodeMalformed_RejectsBeforeLookingAnythingUp()
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            _sut.VerifyEmailAsync(new VerifyEmailRequest { Email = "user@test.com", Code = "12" }));

        _userManagerMock.Verify(m => m.FindByEmailAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task VerifyEmailAsync_WhenCodeWrong_ThrowsValidationException_AndCountsAFailedAttempt()
    {
        ApplicationUser user = CreateUser();
        _userManagerMock.Setup(m => m.FindByEmailAsync(user.Email!)).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.IsLockedOutAsync(user)).ReturnsAsync(false);
        _userManagerMock
            .Setup(m => m.ConfirmEmailAsync(user, "000000"))
            .ReturnsAsync(IdentityResult.Failed(new IdentityError { Description = "Invalid token" }));
        _userManagerMock.Setup(m => m.AccessFailedAsync(user)).ReturnsAsync(IdentityResult.Success);

        await Assert.ThrowsAsync<ValidationException>(() =>
            _sut.VerifyEmailAsync(new VerifyEmailRequest { Email = user.Email!, Code = "000000" }));

        _userManagerMock.Verify(m => m.AccessFailedAsync(user), Times.Once);
    }

    [Fact]
    public async Task VerifyEmailAsync_WhenLockedOut_RefusesWithoutCheckingTheCode()
    {
        ApplicationUser user = CreateUser();
        _userManagerMock.Setup(m => m.FindByEmailAsync(user.Email!)).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.IsLockedOutAsync(user)).ReturnsAsync(true);

        await Assert.ThrowsAsync<UnauthorizedException>(() =>
            _sut.VerifyEmailAsync(new VerifyEmailRequest { Email = user.Email!, Code = "123456" }));

        _userManagerMock.Verify(m => m.ConfirmEmailAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task VerifyEmailAsync_WhenCodeRight_ReturnsAuthResponse_AndClearsFailedAttempts()
    {
        ApplicationUser user = CreateUser();
        _userManagerMock.Setup(m => m.FindByEmailAsync(user.Email!)).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.IsLockedOutAsync(user)).ReturnsAsync(false);
        _userManagerMock.Setup(m => m.ConfirmEmailAsync(user, "123456")).ReturnsAsync(IdentityResult.Success);
        _userManagerMock.Setup(m => m.ResetAccessFailedCountAsync(user)).ReturnsAsync(IdentityResult.Success);

        AuthResponse response = await _sut.VerifyEmailAsync(
            new VerifyEmailRequest { Email = user.Email!, Code = "123456" });

        Assert.Equal("test-jwt-token", response.Token);
        Assert.Equal(user.Id, response.UserId);
        Assert.Equal(user.Email, response.Email);
        _userManagerMock.Verify(m => m.ResetAccessFailedCountAsync(user), Times.Once);
    }

    [Fact]
    public async Task VerifyEmailAsync_AcceptsTheCodeTypedWithSpaces()
    {
        ApplicationUser user = CreateUser();
        _userManagerMock.Setup(m => m.FindByEmailAsync(user.Email!)).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.IsLockedOutAsync(user)).ReturnsAsync(false);
        _userManagerMock.Setup(m => m.ConfirmEmailAsync(user, "123456")).ReturnsAsync(IdentityResult.Success);
        _userManagerMock.Setup(m => m.ResetAccessFailedCountAsync(user)).ReturnsAsync(IdentityResult.Success);

        await _sut.VerifyEmailAsync(new VerifyEmailRequest { Email = user.Email!, Code = " 123 456 " });

        _userManagerMock.Verify(m => m.ConfirmEmailAsync(user, "123456"), Times.Once);
    }

    [Fact]
    public async Task VerifyEmailAsync_InDevelopment_AcceptsTheConfiguredCode_WithoutAskingIdentity()
    {
        _envMock.SetupGet(e => e.EnvironmentName).Returns("Development");
        ApplicationUser user = CreateUser();
        _userManagerMock.Setup(m => m.FindByEmailAsync(user.Email!)).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.IsLockedOutAsync(user)).ReturnsAsync(false);
        _userManagerMock.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);
        _userManagerMock.Setup(m => m.ResetAccessFailedCountAsync(user)).ReturnsAsync(IdentityResult.Success);

        AuthResponse response = await _sut.VerifyEmailAsync(
            new VerifyEmailRequest { Email = user.Email!, Code = "222222" });

        Assert.True(user.EmailConfirmed);
        Assert.Equal(user.Id, response.UserId);
        _userManagerMock.Verify(m => m.ConfirmEmailAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task VerifyEmailAsync_OutsideDevelopment_TreatsTheConfiguredCodeLikeAnyOther()
    {
        // The fixture carries the code; only the environment stands between it and production.
        ApplicationUser user = CreateUser();
        _userManagerMock.Setup(m => m.FindByEmailAsync(user.Email!)).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.IsLockedOutAsync(user)).ReturnsAsync(false);
        _userManagerMock
            .Setup(m => m.ConfirmEmailAsync(user, "222222"))
            .ReturnsAsync(IdentityResult.Failed(new IdentityError { Description = "Invalid token" }));
        _userManagerMock.Setup(m => m.AccessFailedAsync(user)).ReturnsAsync(IdentityResult.Success);

        await Assert.ThrowsAsync<ValidationException>(() =>
            _sut.VerifyEmailAsync(new VerifyEmailRequest { Email = user.Email!, Code = "222222" }));

        Assert.False(user.EmailConfirmed);
        _userManagerMock.Verify(m => m.UpdateAsync(It.IsAny<ApplicationUser>()), Times.Never);
    }

    // ---------------------------------------------------------------------------
    // ResendVerificationEmailAsync
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ResendVerificationEmailAsync_WhenUserNotFound_DoesNotSendEmail()
    {
        _userManagerMock
            .Setup(m => m.FindByEmailAsync("nobody@test.com"))
            .ReturnsAsync((ApplicationUser?)null);

        await _sut.ResendVerificationEmailAsync(
            new ResendVerificationEmailRequest { Email = "nobody@test.com" });

        _emailServiceMock.Verify(
            e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task ResendVerificationEmailAsync_WhenEmailAlreadyConfirmed_DoesNotSendEmail()
    {
        ApplicationUser user = CreateUser(emailConfirmed: true);
        _userManagerMock.Setup(m => m.FindByEmailAsync(user.Email!)).ReturnsAsync(user);

        await _sut.ResendVerificationEmailAsync(
            new ResendVerificationEmailRequest { Email = user.Email! });

        _emailServiceMock.Verify(
            e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task ResendVerificationEmailAsync_WhenUnconfirmed_SendsVerificationEmail()
    {
        ApplicationUser user = CreateUser(emailConfirmed: false);
        _userManagerMock.Setup(m => m.FindByEmailAsync(user.Email!)).ReturnsAsync(user);
        _userManagerMock
            .Setup(m => m.GenerateEmailConfirmationTokenAsync(user))
            .ReturnsAsync("new-token");
        _emailServiceMock
            .Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        await _sut.ResendVerificationEmailAsync(
            new ResendVerificationEmailRequest { Email = user.Email! });

        _emailServiceMock.Verify(
            e => e.SendEmailAsync(user.Email!, It.IsAny<string>(), It.IsAny<string>()),
            Times.Once);
    }

    // ---------------------------------------------------------------------------
    // ForgotPasswordAsync
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ForgotPasswordAsync_WhenUserNotFound_DoesNotSendEmail()
    {
        _userManagerMock
            .Setup(m => m.FindByEmailAsync("nobody@test.com"))
            .ReturnsAsync((ApplicationUser?)null);

        await _sut.ForgotPasswordAsync(new ForgotPasswordRequest { Email = "nobody@test.com" });

        _emailServiceMock.Verify(
            e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task ForgotPasswordAsync_WhenUserExists_SendsResetEmail()
    {
        ApplicationUser user = CreateUser(emailConfirmed: true);
        _userManagerMock.Setup(m => m.FindByEmailAsync(user.Email!)).ReturnsAsync(user);
        _userManagerMock
            .Setup(m => m.GeneratePasswordResetTokenAsync(user))
            .ReturnsAsync("reset-token");
        _emailServiceMock
            .Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        await _sut.ForgotPasswordAsync(new ForgotPasswordRequest { Email = user.Email! });

        _emailServiceMock.Verify(
            e => e.SendEmailAsync(
                user.Email!,
                It.IsAny<string>(),
                It.Is<string>(body => body.Contains("reset-password"))),
            Times.Once);
    }

    [Fact]
    public async Task ForgotPasswordAsync_ResetUrlContainsUserIdAndEncodedToken()
    {
        ApplicationUser user = CreateUser(emailConfirmed: true);
        _userManagerMock.Setup(m => m.FindByEmailAsync(user.Email!)).ReturnsAsync(user);
        _userManagerMock
            .Setup(m => m.GeneratePasswordResetTokenAsync(user))
            .ReturnsAsync("raw+token=");
        _emailServiceMock
            .Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        await _sut.ForgotPasswordAsync(new ForgotPasswordRequest { Email = user.Email! });

        _emailServiceMock.Verify(
            e => e.SendEmailAsync(
                user.Email!,
                It.IsAny<string>(),
                It.Is<string>(body =>
                    body.Contains(user.Id) &&
                    body.Contains(Uri.EscapeDataString("raw+token=")))),
            Times.Once);
    }

    // ---------------------------------------------------------------------------
    // ResetPasswordAsync
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ResetPasswordAsync_WhenUserNotFound_ThrowsNotFoundException()
    {
        _userManagerMock
            .Setup(m => m.FindByIdAsync("missing"))
            .ReturnsAsync((ApplicationUser?)null);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _sut.ResetPasswordAsync(new ResetPasswordRequest
            {
                UserId = "missing",
                Token = "tok",
                NewPassword = "NewPass1!",
                ConfirmNewPassword = "NewPass1!",
            }));
    }

    [Fact]
    public async Task ResetPasswordAsync_WhenTokenInvalid_ThrowsValidationException()
    {
        ApplicationUser user = CreateUser();
        _userManagerMock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        _userManagerMock
            .Setup(m => m.ResetPasswordAsync(user, "bad", "NewPass1!"))
            .ReturnsAsync(IdentityResult.Failed(new IdentityError { Description = "Invalid token" }));

        await Assert.ThrowsAsync<ValidationException>(() =>
            _sut.ResetPasswordAsync(new ResetPasswordRequest
            {
                UserId = user.Id,
                Token = "bad",
                NewPassword = "NewPass1!",
                ConfirmNewPassword = "NewPass1!",
            }));
    }

    [Fact]
    public async Task ResetPasswordAsync_WhenIdentityRejectsThePassword_SurfacesItsWording_NotTheTokenMessage()
    {
        ApplicationUser user = CreateUser(emailConfirmed: true);
        _userManagerMock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        _userManagerMock
            .Setup(m => m.ResetPasswordAsync(user, "tok", "short"))
            .ReturnsAsync(IdentityResult.Failed(new IdentityError
            {
                Code = "PasswordTooShort",
                Description = "Passwords must be at least 12 characters.",
            }));

        ValidationException ex = await Assert.ThrowsAsync<ValidationException>(() =>
            _sut.ResetPasswordAsync(new ResetPasswordRequest
            {
                UserId = user.Id,
                Token = "tok",
                NewPassword = "short",
                ConfirmNewPassword = "short",
            }));

        Assert.Contains("12 characters", string.Join(" ", ex.Errors ?? []));
        Assert.DoesNotContain("token", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResetPasswordAsync_WhenTokenIsDead_SaysSo()
    {
        ApplicationUser user = CreateUser(emailConfirmed: true);
        _userManagerMock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        _userManagerMock
            .Setup(m => m.ResetPasswordAsync(user, "stale", It.IsAny<string>()))
            .ReturnsAsync(IdentityResult.Failed(new IdentityError
            {
                Code = nameof(IdentityErrorDescriber.InvalidToken),
                Description = "Invalid token.",
            }));

        ValidationException ex = await Assert.ThrowsAsync<ValidationException>(() =>
            _sut.ResetPasswordAsync(new ResetPasswordRequest
            {
                UserId = user.Id,
                Token = "stale",
                NewPassword = "a perfectly fine passphrase",
                ConfirmNewPassword = "a perfectly fine passphrase",
            }));

        Assert.Contains("expired", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResetPasswordAsync_WhenTokenValid_CompletesSuccessfully()
    {
        ApplicationUser user = CreateUser();
        _userManagerMock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        _userManagerMock
            .Setup(m => m.ResetPasswordAsync(user, "good-token", "NewPass1!"))
            .ReturnsAsync(IdentityResult.Success);

        await _sut.ResetPasswordAsync(new ResetPasswordRequest
        {
            UserId = user.Id,
            Token = "good-token",
            NewPassword = "NewPass1!",
            ConfirmNewPassword = "NewPass1!",
        });

        _userManagerMock.Verify(m => m.ResetPasswordAsync(user, "good-token", "NewPass1!"), Times.Once);
    }

    // ---------------------------------------------------------------------------
    // ChangePasswordAsync
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ChangePasswordAsync_WhenUserNotFound_ThrowsNotFoundException()
    {
        _userManagerMock
            .Setup(m => m.FindByIdAsync("unknown"))
            .ReturnsAsync((ApplicationUser?)null);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _sut.ChangePasswordAsync("unknown", new ChangePasswordRequest
            {
                CurrentPassword = "OldPass1!",
                NewPassword = "NewPass1!",
                ConfirmNewPassword = "NewPass1!",
            }));
    }

    [Fact]
    public async Task ChangePasswordAsync_WhenCurrentPasswordWrong_ThrowsValidationException()
    {
        ApplicationUser user = CreateUser();
        _userManagerMock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        _userManagerMock
            .Setup(m => m.ChangePasswordAsync(user, "WrongOld1!", "NewPass1!"))
            .ReturnsAsync(IdentityResult.Failed(new IdentityError { Description = "Wrong password" }));

        await Assert.ThrowsAsync<ValidationException>(() =>
            _sut.ChangePasswordAsync(user.Id, new ChangePasswordRequest
            {
                CurrentPassword = "WrongOld1!",
                NewPassword = "NewPass1!",
                ConfirmNewPassword = "NewPass1!",
            }));
    }

    [Fact]
    public async Task ChangePasswordAsync_WhenIdentityRejectsTheNewPassword_SurfacesItsWording()
    {
        ApplicationUser user = CreateUser(emailConfirmed: true);
        _userManagerMock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        _userManagerMock
            .Setup(m => m.ChangePasswordAsync(user, "current", "common"))
            .ReturnsAsync(IdentityResult.Failed(new IdentityError
            {
                Code = "PasswordTooCommon",
                Description = "That password is too common.",
            }));

        ValidationException ex = await Assert.ThrowsAsync<ValidationException>(() =>
            _sut.ChangePasswordAsync(user.Id, new ChangePasswordRequest
            {
                CurrentPassword = "current",
                NewPassword = "common",
                ConfirmNewPassword = "common",
            }));

        Assert.Contains("too common", string.Join(" ", ex.Errors ?? []));
    }

    [Fact]
    public async Task ChangePasswordAsync_WhenCurrentPasswordWrong_SaysSo_ByErrorCode()
    {
        ApplicationUser user = CreateUser(emailConfirmed: true);
        _userManagerMock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        _userManagerMock
            .Setup(m => m.ChangePasswordAsync(user, "wrong", It.IsAny<string>()))
            .ReturnsAsync(IdentityResult.Failed(new IdentityError
            {
                Code = nameof(IdentityErrorDescriber.PasswordMismatch),
                Description = "Incorrect password.",
            }));

        ValidationException ex = await Assert.ThrowsAsync<ValidationException>(() =>
            _sut.ChangePasswordAsync(user.Id, new ChangePasswordRequest
            {
                CurrentPassword = "wrong",
                NewPassword = "a perfectly fine passphrase",
                ConfirmNewPassword = "a perfectly fine passphrase",
            }));

        Assert.Contains("Current password is incorrect", ex.Message);
    }

    [Fact]
    public async Task ChangePasswordAsync_WhenCurrentPasswordCorrect_CompletesSuccessfully()
    {
        ApplicationUser user = CreateUser();
        _userManagerMock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        _userManagerMock
            .Setup(m => m.ChangePasswordAsync(user, "OldPass1!", "NewPass1!"))
            .ReturnsAsync(IdentityResult.Success);

        await _sut.ChangePasswordAsync(user.Id, new ChangePasswordRequest
        {
            CurrentPassword = "OldPass1!",
            NewPassword = "NewPass1!",
            ConfirmNewPassword = "NewPass1!",
        });

        _userManagerMock.Verify(m => m.ChangePasswordAsync(user, "OldPass1!", "NewPass1!"), Times.Once);
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static ApplicationUser CreateUser(bool emailConfirmed = false) =>
        new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            Email = "user@test.com",
            UserName = "user@test.com",
            BusinessName = "Test Business",
            EmailConfirmed = emailConfirmed,
        };
}
