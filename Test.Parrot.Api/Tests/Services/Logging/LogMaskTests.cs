using System.Text.Json;
using Parrot.Application.Logging;

namespace Test.Parrot.Api.Tests.Services.Logging;

// Contact details in logs: enough to recognise what was sent, never the whole value.
public class LogMaskTests
{
    [Theory]
    [InlineData("+234 803 123 4567", "+234******4567")]
    [InlineData("08031234567", "080****4567")]
    [InlineData("1234567", "*****67")]
    [InlineData(null, null)]
    [InlineData("", "")]
    public void Phone_KeepsTheCountryCodeAndLastDigits(string? phone, string? expected)
    {
        Assert.Equal(expected, LogMask.Phone(phone));
    }

    [Theory]
    [InlineData("adire.house@gmail.com", "ad***@gmail.com")]
    [InlineData("a@shop.ng", "a***@shop.ng")]
    [InlineData("not-an-email", "not-an***")]
    [InlineData(null, null)]
    public void Email_KeepsTheStartAndTheDomain(string? email, string? expected)
    {
        Assert.Equal(expected, LogMask.Email(email));
    }

    [Theory]
    [InlineData("14 Bode Thomas Street, Surulere", "14 Bod***")]
    [InlineData("Lagos", "Lagos")]
    public void Text_KeepsTheStart(string text, string expected)
    {
        Assert.Equal(expected, LogMask.Text(text));
    }

    [Fact]
    public void ProfileJson_MasksContactFieldsAndLeavesTheRest()
    {
        string profile = """{"about":"Hand-dyed adire","email":"adire.house@gmail.com","address":"14 Bode Thomas Street","phone_number":"+234 803 123 4567","websites":["https://adirehouse.ng"]}""";

        using JsonDocument masked = JsonDocument.Parse(LogMask.ProfileJson(profile)!);
        JsonElement root = masked.RootElement;

        Assert.Equal("Hand-dyed adire", root.GetProperty("about").GetString());
        Assert.Equal("ad***@gmail.com", root.GetProperty("email").GetString());
        Assert.Equal("14 Bod***", root.GetProperty("address").GetString());
        Assert.Equal("+234******4567", root.GetProperty("phone_number").GetString());
        Assert.Equal("https://adirehouse.ng", root.GetProperty("websites")[0].GetString());
    }

    [Fact]
    public void ProfileJson_WhenNotAnObject_KeepsOnlyTheStart()
    {
        Assert.Equal("plain ***", LogMask.ProfileJson("plain text, not JSON"));
        Assert.Null(LogMask.ProfileJson(null));
    }
}
