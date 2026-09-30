using Microsoft.AspNetCore.Identity;
using Moq;
using Parrot.Application.Auth;
using Parrot.Application.DTOs.Auth;
using Parrot.Application.Logging;
using Parrot.Application.Models;
using Parrot.Application.Profile;
using Parrot.Domain.Exceptions;

namespace Test.Parrot.Api.Tests.Services.Auth;

public class ProfileServiceTests
{
    private readonly Mock<UserManager<ApplicationUser>> _userManagerMock;
    private readonly Mock<IAppLogger<ProfileService>> _loggerMock;
    private readonly ProfileService _sut;

    public ProfileServiceTests()
    {
        Mock<IUserStore<ApplicationUser>> store = new Mock<IUserStore<ApplicationUser>>();
        _userManagerMock = new Mock<UserManager<ApplicationUser>>(
            store.Object, null!, null!, null!, null!, null!, null!, null!, null!);

        _loggerMock = new Mock<IAppLogger<ProfileService>>();
        _loggerMock
            .Setup(l => l.BeginEntityScope(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Mock.Of<IDisposable>());

        _sut = new ProfileService(_userManagerMock.Object, _loggerMock.Object);
    }

    // ---------------------------------------------------------------------------
    // GetAsync
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task GetAsync_ReturnsTheAccountWithThePhoneFormatted()
    {
        ApplicationUser user = NewUser();
        _userManagerMock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);

        ProfileResponse response = await _sut.GetAsync(user.Id);

        Assert.Equal(user.Id, response.UserId);
        Assert.Equal(user.Email, response.Email);
        Assert.False(response.SetupComplete);
    }

    [Fact]
    public async Task GetAsync_WhenAccountIsMissing_ThrowsNotFound()
    {
        _userManagerMock.Setup(m => m.FindByIdAsync(It.IsAny<string>())).ReturnsAsync((ApplicationUser?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.GetAsync("no-such-id"));
    }

    // ---------------------------------------------------------------------------
    // UpdateAsync — the setup step
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task UpdateAsync_SavesTheBusinessNameAndReportsSetupComplete()
    {
        ApplicationUser user = NewUser();
        _userManagerMock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);
        UsingPhoneBook();

        ProfileResponse response = await _sut.UpdateAsync(user.Id, new UpdateProfileRequest
        {
            BusinessName = "  Oma's Kitchen  ",
        });

        Assert.Equal("Oma's Kitchen", response.BusinessName);
        Assert.True(response.SetupComplete);
    }

    [Fact]
    public async Task UpdateAsync_StoresTheTwoColumnsAndReturnsTheNumberFormatted()
    {
        ApplicationUser user = NewUser();
        _userManagerMock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);
        UsingPhoneBook();

        ProfileResponse response = await _sut.UpdateAsync(user.Id, new UpdateProfileRequest
        {
            CountryCode = "234",
            Number = "08012345678",
        });

        Assert.Equal("234", user.PhoneDialCode);
        Assert.Equal("8012345678", user.PhoneNumber);
        Assert.Equal("+2348012345678", response.PhoneNumber);
    }

    [Theory]
    [InlineData("08012345678")]
    [InlineData("8012345678")]
    [InlineData("2348012345678")]
    [InlineData("+2348012345678")]
    public async Task UpdateAsync_AcceptsEveryFormatOfTheSameNumber(string typed)
    {
        ApplicationUser user = NewUser();
        _userManagerMock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);
        UsingPhoneBook();

        await _sut.UpdateAsync(user.Id, new UpdateProfileRequest
        {
            CountryCode = "234",
            Number = typed,
        });

        Assert.Equal("234", user.PhoneDialCode);
        Assert.Equal("8012345678", user.PhoneNumber);
    }

    [Fact]
    public async Task UpdateAsync_WhenAnotherAccountHasTheNumber_IsRefused()
    {
        ApplicationUser user = NewUser();
        ApplicationUser other = NewUser();
        other.PhoneDialCode = "234";
        other.PhoneNumber = "8012345678";

        _userManagerMock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        UsingPhoneBook(user, other);

        ConflictException ex = await Assert.ThrowsAsync<ConflictException>(() =>
            _sut.UpdateAsync(user.Id, new UpdateProfileRequest
            {
                CountryCode = "234",
                Number = "08012345678",
            }));

        Assert.Equal(AuthMessages.PhoneNumberTaken, ex.Message);
        _userManagerMock.Verify(m => m.UpdateAsync(It.IsAny<ApplicationUser>()), Times.Never);
    }

    // Sign-in with a local number searches the national column alone, so that
    // column is held by one account across the whole table — even when the two
    // accounts say different countries.
    [Fact]
    public async Task UpdateAsync_WhenTheNationalNumberIsHeldElsewhere_IsRefused()
    {
        ApplicationUser user = NewUser();
        ApplicationUser other = NewUser();
        other.PhoneDialCode = "44";
        other.PhoneNumber = "8012345678";

        _userManagerMock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        UsingPhoneBook(user, other);

        ConflictException ex = await Assert.ThrowsAsync<ConflictException>(() =>
            _sut.UpdateAsync(user.Id, new UpdateProfileRequest
            {
                CountryCode = "234",
                Number = "08012345678",
            }));

        Assert.Equal(AuthMessages.PhoneNumberTaken, ex.Message);
    }

    [Fact]
    public async Task UpdateAsync_KeepsYourOwnNumberWhenItDoesNotChange()
    {
        ApplicationUser user = NewUser();
        user.PhoneDialCode = "234";
        user.PhoneNumber = "8012345678";

        _userManagerMock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);
        UsingPhoneBook(user);

        await _sut.UpdateAsync(user.Id, new UpdateProfileRequest
        {
            CountryCode = "234",
            Number = "08012345678",
        });

        Assert.Equal("8012345678", user.PhoneNumber);
    }

    // A plus writes the country out loud. If it is not the country chosen in
    // the picker the two disagree, and the stored pair would not be the number
    // the person gave — so the save is refused instead.
    [Fact]
    public async Task UpdateAsync_WithAPlusNumberFromAnotherCountry_IsRefused()
    {
        ApplicationUser user = NewUser();
        _userManagerMock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        UsingPhoneBook();

        ValidationException ex = await Assert.ThrowsAsync<ValidationException>(() =>
            _sut.UpdateAsync(user.Id, new UpdateProfileRequest
            {
                CountryCode = "234",
                Number = "+442071234567",
            }));

        Assert.Equal(AuthMessages.InvalidPhoneNumber, ex.Message);
        _userManagerMock.Verify(m => m.UpdateAsync(It.IsAny<ApplicationUser>()), Times.Never);
    }

    // A dial code is one to three digits and never begins with a zero. Anything
    // else is not a country, whatever it claims to be.
    [Theory]
    [InlineData("2345")]
    [InlineData("0")]
    [InlineData("gb")]
    public async Task UpdateAsync_WithACountryCodeThatIsNotADialCode_IsRefused(string dial)
    {
        ApplicationUser user = NewUser();
        _userManagerMock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        UsingPhoneBook();

        ValidationException ex = await Assert.ThrowsAsync<ValidationException>(() =>
            _sut.UpdateAsync(user.Id, new UpdateProfileRequest
            {
                CountryCode = dial,
                Number = "8012345678",
            }));

        Assert.Equal(AuthMessages.InvalidDialCode, ex.Message);
    }

    // The number arrived without the country it belongs to, so there is nothing
    // to store it against — and the fix is to pick a country, not to guess.
    [Fact]
    public async Task UpdateAsync_WithANumberButNoCountry_AsksForTheCountry()
    {
        ApplicationUser user = NewUser();
        _userManagerMock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        UsingPhoneBook();

        ValidationException ex = await Assert.ThrowsAsync<ValidationException>(() =>
            _sut.UpdateAsync(user.Id, new UpdateProfileRequest { Number = "08012345678" }));

        Assert.Equal(AuthMessages.InvalidDialCode, ex.Message);
    }

    [Fact]
    public async Task UpdateAsync_LeavesFieldsAloneWhenTheyAreNotSent()
    {
        // A partial save must not clear what an earlier step already wrote.
        ApplicationUser user = NewUser();
        user.BusinessName = "Already Set";
        _userManagerMock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);
        UsingPhoneBook();

        ProfileResponse response = await _sut.UpdateAsync(user.Id, new UpdateProfileRequest
        {
            LogoUrl = "https://cdn.example.com/logo.png",
        });

        Assert.Equal("Already Set", response.BusinessName);
        Assert.Equal("https://cdn.example.com/logo.png", response.LogoUrl);
    }

    // ---------------------------------------------------------------------------
    // UpdateAsync — the settings page
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task UpdateAsync_SavesTheNamesTrimmed()
    {
        ApplicationUser user = NewUser();
        _userManagerMock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);
        UsingPhoneBook();

        ProfileResponse response = await _sut.UpdateAsync(user.Id, new UpdateProfileRequest
        {
            FirstName = "  Seun ",
            LastName = " Amole  ",
        });

        Assert.Equal("Seun", response.FirstName);
        Assert.Equal("Amole", response.LastName);
    }

    [Fact]
    public async Task UpdateAsync_WithEmptyNamesAndLogo_ClearsThem()
    {
        ApplicationUser user = NewUser();
        user.FirstName = "Seun";
        user.LastName = "Amole";
        user.LogoUrl = "https://cdn.example.com/logo.png";
        _userManagerMock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);
        UsingPhoneBook();

        ProfileResponse response = await _sut.UpdateAsync(user.Id, new UpdateProfileRequest
        {
            FirstName = "",
            LastName = "   ",
            LogoUrl = "",
        });

        Assert.Null(response.FirstName);
        Assert.Null(response.LastName);
        Assert.Null(response.LogoUrl);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://cdn.example.com/logo.png")]
    [InlineData("/relative/logo.png")]
    public async Task UpdateAsync_WithALogoThatIsNotAWebUrl_IsRefused(string logoUrl)
    {
        ApplicationUser user = NewUser();
        _userManagerMock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        UsingPhoneBook();

        ValidationException ex = await Assert.ThrowsAsync<ValidationException>(() =>
            _sut.UpdateAsync(user.Id, new UpdateProfileRequest { LogoUrl = logoUrl }));

        Assert.Equal(AuthMessages.InvalidLogoUrl, ex.Message);
    }

    [Fact]
    public async Task UpdateAsync_WithANameOver80Characters_IsRefused()
    {
        ApplicationUser user = NewUser();
        _userManagerMock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        UsingPhoneBook();

        ValidationException ex = await Assert.ThrowsAsync<ValidationException>(() =>
            _sut.UpdateAsync(user.Id, new UpdateProfileRequest { FirstName = new string('a', 81) }));

        Assert.Equal(AuthMessages.NameTooLong, ex.Message);
    }

    [Fact]
    public async Task UpdateAsync_WithNothingSent_ChangesNothing()
    {
        // No field is required: an empty body is a valid save.
        ApplicationUser user = NewUser();
        user.FirstName = "Seun";
        user.BusinessName = "SeunFoods";
        user.LogoUrl = "https://cdn.example.com/logo.png";
        _userManagerMock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);
        UsingPhoneBook();

        ProfileResponse response = await _sut.UpdateAsync(user.Id, new UpdateProfileRequest());

        Assert.Equal("Seun", response.FirstName);
        Assert.Equal("SeunFoods", response.BusinessName);
        Assert.Equal("https://cdn.example.com/logo.png", response.LogoUrl);
        Assert.Equal("user@test.com", response.Email);
    }

    [Fact]
    public async Task UpdateAsync_WhenIdentityRejectsTheSave_SurfacesItsReason()
    {
        ApplicationUser user = NewUser();
        _userManagerMock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        _userManagerMock
            .Setup(m => m.UpdateAsync(user))
            .ReturnsAsync(IdentityResult.Failed(new IdentityError { Code = "Stale", Description = "That account changed elsewhere." }));
        UsingPhoneBook();

        ValidationException ex = await Assert.ThrowsAsync<ValidationException>(() =>
            _sut.UpdateAsync(user.Id, new UpdateProfileRequest { BusinessName = "Oma's Kitchen" }));

        Assert.Contains("That account changed elsewhere.", ex.Errors);
    }

    // ---------------------------------------------------------------------------

    private void UsingPhoneBook(params ApplicationUser[] users) =>
        _userManagerMock.SetupGet(m => m.Users).Returns(users.AsQueryable());

    private static ApplicationUser NewUser() =>
        new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            Email = "user@test.com",
            UserName = "user@test.com",
        };
}
