using Parrot.Application.Messaging;
using Parrot.Domain.Enums;

namespace Test.Parrot.Api.Tests.Services.Messaging;

public class ConversationKeyTests
{
    private const string Customer = "2348012345678";
    private const string Business = "2349011112222";

    [Fact]
    public void Create_EitherDirection_GivesTheSameKey()
    {
        Assert.Equal(
            ConversationKey.Create(MessagingPlatform.WhatsApp, Customer, Business),
            ConversationKey.Create(MessagingPlatform.WhatsApp, Business, Customer));
    }

    [Fact]
    public void Create_IsThePlatformThenTheSortedParticipants()
    {
        Assert.Equal(
            "whatsapp:2348012345678:2349011112222",
            ConversationKey.Create(MessagingPlatform.WhatsApp, Business, Customer));
    }

    // Meta sends the customer as bare digits and the business number formatted
    // for display; both must land on one participant.
    [Theory]
    [InlineData("2348012345678")]
    [InlineData("+2348012345678")]
    [InlineData("+234 801 234 5678")]
    [InlineData("+234-801-234-5678")]
    public void Create_WhatsAppNumbersInAnyDisplayShape_GiveTheSameKey(string customer)
    {
        Assert.Equal(
            "whatsapp:2348012345678:2349011112222",
            ConversationKey.Create(MessagingPlatform.WhatsApp, customer, "+234 901 111 2222"));
    }

    [Fact]
    public void Create_Facebook_UsesTheTrimmedIdsSorted()
    {
        Assert.Equal(
            "facebook:1029384756:5647382910",
            ConversationKey.Create(MessagingPlatform.Facebook, " 5647382910 ", "1029384756"));
    }

    [Fact]
    public void Create_TikTok_KeepsTheOpenIdsExactly_SortedUnderItsOwnPrefix()
    {
        Assert.Equal(
            "tiktok:-000Abc:-000Zed",
            ConversationKey.Create(MessagingPlatform.TikTok, " -000Zed ", "-000Abc"));
        Assert.NotEqual(
            ConversationKey.Create(MessagingPlatform.Facebook, Customer, Business),
            ConversationKey.Create(MessagingPlatform.TikTok, Customer, Business));
    }

    [Fact]
    public void Create_TheSameIdsOnDifferentPlatforms_NameDifferentConversations()
    {
        Assert.NotEqual(
            ConversationKey.Create(MessagingPlatform.WhatsApp, Customer, Business),
            ConversationKey.Create(MessagingPlatform.Facebook, Customer, Business));
    }

    // A customer with a WhatsApp username can arrive as a business-scoped user
    // id instead of a number. Keeping only its digits would merge customers.
    [Fact]
    public void Create_WhatsAppUserIdThatIsNotANumber_IsKeptExactly()
    {
        Assert.Equal(
            "whatsapp:2349011112222:NG.1A2B3C4D",
            ConversationKey.Create(MessagingPlatform.WhatsApp, " NG.1A2B3C4D ", Business));
    }

    [Fact]
    public void Create_WhatsAppUserIdsSharingDigits_NameDifferentConversations()
    {
        Assert.NotEqual(
            ConversationKey.Create(MessagingPlatform.WhatsApp, "NG.12A34", Business),
            ConversationKey.Create(MessagingPlatform.WhatsApp, "NG.12B34", Business));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Create_WithAnEmptyParticipant_IsRefused(string? participant)
    {
        Assert.Throws<ArgumentException>(() =>
            ConversationKey.Create(MessagingPlatform.WhatsApp, participant!, Business));
    }
}
