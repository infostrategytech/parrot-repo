using Parrot.Application.Auth;

namespace Test.Parrot.Api.Tests.Services.Auth;

public class PhoneNormalizerTests
{
    private const string Nigeria = "234";

    // The setup screen hands over the country as well as the number, so every
    // shape a person would type lands on the same national part — and that part
    // is what the two columns store.
    [Theory]
    [InlineData("08012345678")]
    [InlineData("8012345678")]
    [InlineData("2348012345678")]
    [InlineData("+2348012345678")]
    public void ToNational_AllWaysOfTypingTheSameNumber_GiveTheSameNationalPart(string typed)
    {
        Assert.Equal("8012345678", PhoneNormalizer.ToNational(typed, Nigeria));
    }

    [Fact]
    public void ToNational_KeepsSpacesAndDashesOut()
    {
        Assert.Equal("8012345678", PhoneNormalizer.ToNational("+234 801-234-5678", Nigeria));
    }

    [Fact]
    public void ToNational_UsesTheStatedCountryRatherThanGuessing()
    {
        // The country is declared, not worked out of the digits, so 44 stays 44
        // even though this deployment's own code is 234.
        Assert.Equal("2071234567", PhoneNormalizer.ToNational("+442071234567", "44"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("abc")]
    public void ToNational_WhenTheNumberCannotBeAPhone_ReturnsNull(string? typed)
    {
        Assert.Null(PhoneNormalizer.ToNational(typed, Nigeria));
    }

    // The number is exactly the country code and nothing else — there is no
    // national part left, so there is nothing to store.
    [Fact]
    public void ToNational_WhenTheNumberIsOnlyTheCountry_ReturnsNull()
    {
        Assert.Null(PhoneNormalizer.ToNational("234", Nigeria));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("2345")]
    public void ToNational_WhenTheStatedCountryIsNotAPlausibleDialCode_ReturnsNull(string? dial)
    {
        Assert.Null(PhoneNormalizer.ToNational("08012345678", dial));
    }

    // E.164 stops at fifteen digits including the country code, and the country
    // and the national part are counted together, not one at a time.
    [Theory]
    [InlineData("012345678901234")] // written locally
    [InlineData("234123456789012345678")] // carries the dial code
    [InlineData("12345678901234")] // bare national digits
    public void ToNational_WhenTheNumberOutgrowsE164_ReturnsNull(string typed)
    {
        Assert.Null(PhoneNormalizer.ToNational(typed, Nigeria));
    }

    // Sign-in gets one field and no country. A local number is searched as the
    // national column; an international one is searched as the two stored
    // columns joined. Nothing is split, because nothing needs to be.
    [Theory]
    [InlineData("08012345678", "8012345678")]
    [InlineData("8012345678", "8012345678")]
    [InlineData("2348012345678", "2348012345678")]
    [InlineData("+2348012345678", "2348012345678")]
    public void SearchValue_LeavesTheNumberInTheShapeItIsComparedAgainst(string typed, string expected)
    {
        Assert.Equal(expected, PhoneNormalizer.SearchValue(typed));
    }

    [Fact]
    public void SearchValue_KeepsSpacesAndDashesOut()
    {
        Assert.Equal("2348012345678", PhoneNormalizer.SearchValue("+234 801-234-5678"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("abc")]
    [InlineData("not a number at all")]
    public void SearchValue_WhenItCannotBeAPhone_ReturnsNull(string? typed)
    {
        Assert.Null(PhoneNormalizer.SearchValue(typed));
    }

    [Theory]
    [InlineData("012345678901234567")] // written locally
    [InlineData("234123456789012345678")] // carries the dial code
    public void SearchValue_WhenTheNumberOutgrowsE164_ReturnsNull(string typed)
    {
        Assert.Null(PhoneNormalizer.SearchValue(typed));
    }

    // The whole sign-in contract in one property: whatever the setup screen
    // stored for this number, one of the two values sign-in searches on is an
    // exact match for it. That is why the two columns are compared rather than
    // cut into pieces.
    [Theory]
    [InlineData("08012345678")]
    [InlineData("8012345678")]
    [InlineData("2348012345678")]
    [InlineData("+2348012345678")]
    public void SearchValue_EitherMatchesTheStoredNationalPartOrTheStoredPair(string typed)
    {
        string national = PhoneNormalizer.ToNational(typed, Nigeria)!;
        string? search = PhoneNormalizer.SearchValue(typed);

        Assert.True(
            search == national || search == Nigeria + national,
            $"Expected {search} to match {national} or {Nigeria}{national}");
    }

    [Fact]
    public void ToDisplay_RejoinsTheTwoColumns()
    {
        Assert.Equal("+2348012345678", PhoneNormalizer.ToDisplay("234", "8012345678"));
    }

    [Theory]
    [InlineData(null, "8012345678")]
    [InlineData("234", null)]
    [InlineData("", "8012345678")]
    [InlineData("234", "")]
    public void ToDisplay_WhenEitherHalfIsMissing_ReturnsNull(string? dial, string? national)
    {
        Assert.Null(PhoneNormalizer.ToDisplay(dial, national));
    }
}
