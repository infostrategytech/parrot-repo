using Parrot.SharedComponents.DesignSystem;

namespace Test.Parrot.Api.Tests.Web;

// Phone numbers and emails, checked one way on every screen (ContactText): the length a number should have,
// said in words that say how to fix it.
public class ContactTextTests
{
    [Theory]
    [InlineData("0803 555 1234", "+2348035551234", true)]
    [InlineData("803-555-1234", "+2348035551234", true)]
    [InlineData("(0803) 555 1234", "+2348035551234", true)]
    [InlineData("+234 803 555 1234", "+2348035551234", false)]
    [InlineData("234 803 555 1234", "+2348035551234", false)]
    [InlineData("00234 803 555 1234", "+2348035551234", false)]
    [InlineData("+44 20 7946 0958", "+442079460958", false)]
    public void Numbers_AsPeopleWriteThem_BecomeOneShape(string typed, string saved, bool local)
    {
        var check = ContactText.Phone(typed);

        Assert.Equal(saved, check.Number);
        Assert.Null(check.Problem);
        Assert.Equal(local, check.Local);
    }

    [Theory]
    [InlineData("0803 555 123", "Number too short", "11 digits")]
    [InlineData("0803 555 12345", "Number too long", "11 digits")]
    [InlineData("803 555 123", "Number too short", "11 digits")]
    [InlineData("+234 803 555 123", "Number too short", "10 digits")]
    [InlineData("+234 803 555 12345", "Number too long", "10 digits")]
    [InlineData("+44 20", "Number too short", "8 to 15 digits")]
    [InlineData("+44 2079 4609 5812 3456", "Number too long", "8 to 15 digits")]
    [InlineData("0603 555 1234", "Check this number", "070, 080")]
    public void AWrongLength_SaysTheLengthItShouldHave(string typed, string problem, string advice)
    {
        var check = ContactText.Phone(typed);

        Assert.Null(check.Number);
        Assert.Equal(problem, check.Problem);
        Assert.Contains(advice, check.Advice);
    }

    [Fact]
    public void NoNumber_IsNoProblem()
    {
        Assert.Null(ContactText.Phone("  ").Problem);
        Assert.Null(ContactText.Phone(null).Advice);
    }

    [Theory]
    [InlineData("ada@kitchen.ng", true)]
    [InlineData("first.last@shop.adire.com.ng", true)]
    [InlineData("  ada@kitchen.ng  ", true)]
    [InlineData("ada@kitchen", false)]
    [InlineData("ada@kitchen.n", false)]
    [InlineData("ada kitchen@shop.com", false)]
    [InlineData("hello there ada@kitchen.ng", false)]
    [InlineData("ada@@kitchen.ng", false)]
    [InlineData("ada@kitchen..ng", false)]
    [InlineData("", false)]
    public void Emails_AreOneWholeAddress(string typed, bool valid) =>
        Assert.Equal(valid, ContactText.IsEmail(typed));
}
