using Parrot.Web.Services;

namespace Test.Parrot.Api.Tests.Services;

public class ChannelLinkTests
{
    [Theory]
    [InlineData("2348012345678", "https://wa.me/2348012345678")]
    [InlineData("+234 801 234 5678", "https://wa.me/2348012345678")]
    [InlineData("002348012345678", "https://wa.me/2348012345678")]
    public void WhatsAppOpensTheCustomersChatFromTheirNumber(string sender, string url)
    {
        ChannelLink link = ChannelLinks.For("whatsapp", sender, null)!;

        Assert.Equal(url, link.Url);
        Assert.True(link.OpensChat);
        Assert.Equal("Open chat in WhatsApp", link.ButtonLabel);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a number")]
    [InlineData("12")]
    public void WhatsAppWithoutAUsableNumberHasNoLink(string sender) =>
        Assert.Null(ChannelLinks.For("whatsapp", sender, null));

    [Fact]
    public void MessengerOpensThePagesInboxWhenThePageIsKnown()
    {
        ChannelLink link = ChannelLinks.For("facebook", "psid-opaque", "1234567890")!;

        Assert.Equal("https://business.facebook.com/latest/inbox/messenger?asset_id=1234567890", link.Url);
        Assert.False(link.OpensChat);
        Assert.Equal("Open Messenger inbox", link.ButtonLabel);
        Assert.Contains("Find Ada there", link.Hint("Ada"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1&evil=1")]
    public void MessengerNeverPutsAnUnexpectedAccountIdInTheAddress(string? accountId) =>
        Assert.Equal("https://business.facebook.com/latest/inbox/messenger", ChannelLinks.For("facebook", "psid", accountId)!.Url);

    [Theory]
    [InlineData("instagram", "https://www.instagram.com/direct/inbox/", "Instagram")]
    [InlineData("tiktok", "https://www.tiktok.com/messages", "TikTok")]
    public void InstagramAndTikTokOpenTheirInboxBecauseTheirSendersAreOpaqueIds(string channel, string url, string platform)
    {
        ChannelLink link = ChannelLinks.For(channel, "opaque-id", "acct")!;

        Assert.Equal(url, link.Url);
        Assert.Equal(platform, link.Platform);
        Assert.False(link.OpensChat);
    }

    [Fact]
    public void MessengerOpensTheCustomersProfileFromTheirNumericId()
    {
        ChannelLink link = ChannelLinks.For("facebook", "6543210987654321", "1234567890")!;

        Assert.Equal("https://www.facebook.com/6543210987654321", link.Url);
        Assert.Equal(ChannelLinkKind.Profile, link.Kind);
        Assert.Equal("Open profile on Messenger", link.ButtonLabel);
    }

    [Fact]
    public void InstagramOpensTheCustomersProfileFromTheirHandle()
    {
        ChannelLink link = ChannelLinks.For("instagram", "1877343696564198", "acct", "ada.love_1")!;

        Assert.Equal("https://www.instagram.com/ada.love_1/", link.Url);
        Assert.Equal(ChannelLinkKind.Profile, link.Kind);
    }

    [Theory]
    [InlineData("Ada Love")]
    [InlineData(null)]
    [InlineData("a/../b")]
    public void InstagramWithoutAUsableHandleOpensTheInbox(string? name) =>
        Assert.Equal(ChannelLinkKind.Inbox, ChannelLinks.For("instagram", "1877343696564198", "acct", name)!.Kind);

    [Fact]
    public void AnUnknownChannelHasNoLink() => Assert.Null(ChannelLinks.For("email", "a@b.c", null));
}
