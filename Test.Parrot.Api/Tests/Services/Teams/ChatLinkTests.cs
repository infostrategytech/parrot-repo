using Parrot.Application.Teams;
using Parrot.Domain.Enums;

namespace Test.Parrot.Api.Tests.Services.Teams;

// The link on the checkout page that takes a customer back to the business's chat.
public sealed class ChatLinkTests
{
    [Theory]
    [InlineData(MessagingPlatform.WhatsApp, "+234 803 123 4567", "https://wa.me/2348031234567")]
    [InlineData(MessagingPlatform.WhatsApp, "15551234567", "https://wa.me/15551234567")]
    [InlineData(MessagingPlatform.Instagram, "@ada.bags", "https://ig.me/m/ada.bags")]
    [InlineData(MessagingPlatform.Instagram, "ada_bags", "https://ig.me/m/ada_bags")]
    [InlineData(MessagingPlatform.Facebook, "102938475610", "https://m.me/102938475610")]
    public void Each_channel_opens_its_own_chat(MessagingPlatform platform, string handle, string link) =>
        Assert.Equal(link, ChatLink.For(platform, handle));

    [Theory]
    [InlineData(MessagingPlatform.WhatsApp, null)]
    [InlineData(MessagingPlatform.WhatsApp, "  ")]
    [InlineData(MessagingPlatform.WhatsApp, "12345")]
    [InlineData(MessagingPlatform.Instagram, "not a/username")]
    [InlineData(MessagingPlatform.Facebook, "ada-bags/../x")]
    [InlineData(MessagingPlatform.TikTok, "ada_bags")]
    public void Without_a_usable_handle_there_is_no_link(MessagingPlatform platform, string? handle) =>
        Assert.Null(ChatLink.For(platform, handle));
}
