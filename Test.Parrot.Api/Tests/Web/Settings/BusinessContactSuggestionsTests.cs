using Parrot.Web.Features.Dashboard.Channels.Models;
using Parrot.Web.Features.Dashboard.Knowledge;
using Parrot.Web.Features.Settings;
using Parrot.Web.Services;
using Xunit;
using Found = Parrot.Web.Features.Settings.BusinessContactSuggestions;

namespace Test.Parrot.Api.Tests.Web.Settings;

// Contact details already given to yára fill the Business profile's empty fields, saying where they came from
// (designer, 7 November 2026).
public sealed class BusinessContactSuggestionsTests
{
    private static BusinessChannel Channel(ChannelId id, string? account, ChannelStatus status = ChannelStatus.Connected) =>
        new() { Id = id, Name = id.ToString(), Description = "", Status = status, AccountName = account, Setup = new ChannelSetupState() };

    private static KnowledgeFact Fact(string text, string? source = null, FactState state = FactState.Known) =>
        new(Guid.NewGuid().ToString("N"), KnowledgeTopic.Notes, text, state, source, DateTimeOffset.UtcNow, "");

    [Fact]
    public void ConnectedChannelsFillTheirOwnFields()
    {
        var found = Found.Find(new BusinessContact(),
            [Channel(ChannelId.Whatsapp, "+234 803 555 1234"), Channel(ChannelId.Instagram, "@adirelagos"), Channel(ChannelId.TikTok, "@adire.tt", ChannelStatus.NotConnected)], []);

        Assert.Contains(found, item => item.Field == Found.WhatsApp && item.Value == "+234 803 555 1234" && item.Source == "From your connected WhatsApp");
        Assert.Contains(found, item => item.Field == Found.Instagram && item.Value == "adirelagos");
        Assert.DoesNotContain(found, item => item.Field == Found.TikTok);
    }

    [Fact]
    public void KnowledgeFillsEmailWebsiteAndNumbersButNotAnEmailsDomainAsAWebsite()
    {
        var found = Found.Find(new BusinessContact(),
            [], [Fact("Email us at hello@adirelagos.com for bulk orders."), Fact("Order on WhatsApp: 0803 555 1234", "your Instagram bio"), Fact("Shop at www.adirelagos.ng")]);

        Assert.Contains(found, item => item.Field == Found.Email && item.Value == "hello@adirelagos.com" && item.Source == "From what you told your AI");
        Assert.Contains(found, item => item.Field == Found.WhatsApp && item.Value == "0803 555 1234" && item.Source == "From your Instagram bio, in Knowledge");
        Assert.Contains(found, item => item.Field == Found.Website && item.Value == "www.adirelagos.ng");
        Assert.DoesNotContain(found, item => item.Field == Found.Website && item.Value.Contains("adirelagos.com"));
    }

    [Fact]
    public void FieldsAlreadyFilledAndUnconfirmedFactsAreLeftAlone()
    {
        var found = Found.Find(new BusinessContact(Instagram: "mine"),
            [Channel(ChannelId.Instagram, "@adirelagos")], [Fact("Write to sales@adire.ng", state: FactState.ToConfirm)]);

        Assert.Empty(found);
    }

    [Fact]
    public void ApplyPutsEachValueInItsField()
    {
        var contact = Found.Apply(new BusinessContact(), [new(Found.WhatsApp, "0803", "x"), new(Found.Email, "a@b.ng", "y")]);

        Assert.Equal("0803", contact.WhatsApp);
        Assert.Equal("a@b.ng", contact.Email);
    }

    [Theory]
    [InlineData("https://www.facebook.com/adirelagos/", "adirelagos")]
    [InlineData("m.facebook.com/adire.lagos", "adire.lagos")]
    [InlineData("adirelagos", "adirelagos")]
    public void AFacebookLinkIsKeptAsThePageName(string pasted, string page) => Assert.Equal(page, BusinessContact.FacebookPage(pasted));

    [Theory]
    [InlineData("", null)]
    [InlineData("hello@adire.ng", null)]
    [InlineData("hello@adire", "Enter an email like hello@yourshop.com.")]
    [InlineData("adire.ng", "Enter an email like hello@yourshop.com.")]
    public void AnEmailThatCantBeOneIsCaught(string email, string? problem) => Assert.Equal(problem, new BusinessContact(Email: email).EmailProblem);
}
