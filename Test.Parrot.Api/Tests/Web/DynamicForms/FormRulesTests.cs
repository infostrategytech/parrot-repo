using Parrot.SharedComponents.DesignSystem;

namespace Test.Parrot.Api.Tests.Web.DynamicForms;

// The rules every form question follows (dynamic-form-contract.md, section 5), and the words when one is broken.
public class FormRulesTests
{
    [Theory]
    [InlineData("08035551234", "+2348035551234")]
    [InlineData("0803 555 1234", "+2348035551234")]
    [InlineData("8035551234", "+2348035551234")]
    [InlineData("2348035551234", "+2348035551234")]
    [InlineData("+234 803 555 1234", "+2348035551234")]
    [InlineData("+44 20 7946 0958", "+442079460958")]
    [InlineData("0803555", null)]
    [InlineData("hello", null)]
    public void APhoneNumber_IsMadeWhole(string typed, string? whole) => Assert.Equal(whole, FormRules.WholePhone(typed));

    [Fact]
    public void AWholeNumber_IsSaidBack() => Assert.Equal("+234 803 555 1234", FormRules.SayPhone("+2348035551234"));

    [Theory]
    [InlineData("45000", 45000)]
    [InlineData("45,000", 45000)]
    [InlineData("₦45,000", 45000)]
    [InlineData("45k", 45000)]
    [InlineData("1.5m", 1500000)]
    public void AnAmount_IsReadAnyCommonWay(string typed, double value) => Assert.Equal((decimal)value, FormRules.ReadNumber(typed));

    [Fact]
    public void ARequiredQuestion_LeftEmpty_SaysSo_AndAnOptionalOneDoesnt()
    {
        Assert.Equal("Answer this to continue.", FormRules.Check(new FormQuestion("q", "Q", FormQuestionKind.Short) { Required = true }, FormAnswer.None));
        Assert.Null(FormRules.Check(new FormQuestion("q", "Q", FormQuestionKind.Short) { Required = false }, FormAnswer.None));
        // Left out, as the form has always worked: no rule.
        Assert.Null(FormRules.Check(new FormQuestion("q", "Q", FormQuestionKind.One), FormAnswer.None));
    }

    [Fact]
    public void EachKind_SaysWhatToFix()
    {
        Assert.Equal("Check this email.", FormRules.Check(new FormQuestion("q", "Q", FormQuestionKind.Email), FormAnswer.None with { Text = "ada@" }));
        Assert.Null(FormRules.Check(new FormQuestion("q", "Q", FormQuestionKind.Email), FormAnswer.None with { Text = "ada@kitchen.ng" }));
        Assert.Equal("Check this link.", FormRules.Check(new FormQuestion("q", "Q", FormQuestionKind.Url), FormAnswer.None with { Text = "not a link" }));
        Assert.Null(FormRules.Check(new FormQuestion("q", "Q", FormQuestionKind.Url), FormAnswer.None with { Text = "instagram.com/adakitchen" }));
        Assert.Equal("Check this number.", FormRules.Check(new FormQuestion("q", "Q", FormQuestionKind.Phone), FormAnswer.None with { Text = "0803" }));
        Assert.Equal("Use a number from 1 to 30.", FormRules.Check(new FormQuestion("q", "Q", FormQuestionKind.Number) { Min = 1, Max = 30 }, FormAnswer.None with { Text = "45", Number = 45 }));
        Assert.Equal("Keep it under 10 characters.", FormRules.Check(new FormQuestion("q", "Q", FormQuestionKind.Short) { MaxLength = 10 }, FormAnswer.None with { Text = "Ada's Kitchen and Grill" }));
        Assert.Equal("Pick at least 2.", FormRules.Check(new FormQuestion("q", "Q", FormQuestionKind.Many, [new("A"), new("B"), new("C")]) { Min = 2 }, new FormAnswer(["A"])));
    }

    [Fact]
    public void TheQuestionsOwnWords_WinOverTheDefaults()
    {
        var question = new FormQuestion("q", "Q", FormQuestionKind.Phone) { Required = true, Errors = new Dictionary<string, string> { ["required"] = "I need a number to reach you.", ["format"] = "Add the country code, such as +234." } };

        Assert.Equal("I need a number to reach you.", FormRules.Check(question, FormAnswer.None));
        Assert.Equal("Add the country code, such as +234.", FormRules.Check(question, FormAnswer.None with { Text = "12" }));
    }

    [Fact]
    public void ANote_IsNeverWrong() => Assert.Null(FormRules.Check(new FormQuestion("n", "Read this", FormQuestionKind.Note) { Required = true }, FormAnswer.None));
}
