using Parrot.Web.Features.Profile;
using Xunit;

namespace Test.Parrot.Api.Tests.Web.Settings;

// The person's own number: shown as people write it, sent as the profile API takes it (a dial code and the
// national digits), and checked before it's sent.
public sealed class ProfilePhoneTests
{
    private static PhoneCountry Code(string code) => PhoneCountries.All.Single(country => country.Code == code);

    [Fact]
    public void ASavedNumberShowsWithItsCountry()
    {
        Assert.Equal((Code("234"), "0803 555 1234"), PhoneCountries.Split("+234 803 555 1234"));
        Assert.Equal((Code("234"), "0803 555 1234"), PhoneCountries.Split("+2348035551234"));
        Assert.Equal((Code("44"), "2079460958"), PhoneCountries.Split("+44 2079460958"));
        Assert.Equal((Code("1"), "4155550100"), PhoneCountries.Split("+1 4155550100"));
        Assert.Equal((PhoneCountries.Home, ""), PhoneCountries.Split(null));
    }

    [Fact]
    public void ItGoesToTheApiAsNationalDigits()
    {
        Assert.Equal("8035551234", PhoneCountries.National(Code("234"), "0803 555 1234"));
        Assert.Equal("8035551234", PhoneCountries.National(Code("234"), "+234 803 555 1234"));
        Assert.Equal("2079460958", PhoneCountries.National(Code("44"), "020 7946 0958"));
        Assert.True(PhoneCountries.Same(Code("234"), "08035551234", Code("234"), "+234 803 555 1234"));
        Assert.False(PhoneCountries.Same(Code("234"), "08035551234", Code("233"), "08035551234"));
    }

    [Fact]
    public void WrongNumbersSayHowToFixThem()
    {
        Assert.Null(PhoneCountries.Problem(Code("234"), "0803 555 1234"));
        Assert.NotNull(PhoneCountries.Problem(Code("234"), "0803 555"));
        Assert.Equal("That number has another country's code. Choose its country first.", PhoneCountries.Problem(Code("234"), "+44 20 7946 0958"));
        Assert.Null(PhoneCountries.Problem(Code("44"), "020 7946 0958"));
        Assert.NotNull(PhoneCountries.Problem(Code("44"), "12ab"));
    }

    [Theory]
    [InlineData("+49 30 12345678")]
    [InlineData("+91 9876543210")]
    [InlineData("+420123456789")]
    public void AnUnlistedCountryKeepsTheSavedNumberInsteadOfGuessingNigeria(string saved)
    {
        var (country, number) = PhoneCountries.Split(saved);
        Assert.Equal(PhoneCountries.Unlisted, country);
        Assert.Equal(saved, number);
        Assert.True(PhoneCountries.Same(country, number, country, saved));
        Assert.NotNull(PhoneCountries.Problem(country, number));
    }

    [Fact]
    public void ChoosingACountryWithNoNumberIsNotAChangeToTheSavedProfile()
    {
        Assert.True(PhoneCountries.Same(Code("44"), "", PhoneCountries.Home, null));
    }
}
