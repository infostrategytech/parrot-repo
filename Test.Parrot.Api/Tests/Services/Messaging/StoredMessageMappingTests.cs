using System.Text.Json;
using Parrot.Application.Messaging;
using Parrot.Domain.Entities.Messages;
using Parrot.Domain.Enums;

namespace Test.Parrot.Api.Tests.Services.Messaging;

// From is always the customer and Receiver always the business, so the shared
// shape must swap them for a reply.
public class StoredMessageMappingTests
{
    [Theory]
    [InlineData(false, "2348012345678", "2349011112222")]
    [InlineData(true, "2349011112222", "2348012345678")]
    public void SenderAndReceiverFollowTheDirection(bool outgoing, string sender, string receiver)
    {
        StoredMessage message = new WhatsAppMessageDocument
        {
            MessageId = "whatsapp:wamid.1", Platform = MessagingPlatform.WhatsApp, ExternalAccountId = "456",
            ChannelId = "123", ChannelDisplayName = "+234 901 111 2222", From = "2348012345678", Timestamp = "0",
            Type = "text", Outgoing = outgoing, Receiver = "2349011112222",
            ConversationKey = "whatsapp:2348012345678:2349011112222", Sequence = 1,
        }.ToStoredMessage();

        Assert.Equal(sender, message.Sender);
        Assert.Equal(receiver, message.Receiver);
    }

    // A shared TikTok post's link is the only content the CRM has to show for it.
    [Fact]
    public void ATikTokMessage_KeepsItsAttachmentLinks()
    {
        StoredMessage message = new TikTokMessageDocument
        {
            MessageId = "tiktok:m1", Platform = MessagingPlatform.TikTok, ExternalAccountId = "biz", ChannelId = "biz",
            ChannelDisplayName = string.Empty, From = "customer", Timestamp = "0", Type = "share_post", Receiver = "biz",
            AttachmentUrls = ["https://www.tiktok.com/embed/1"],
            ConversationKey = "tiktok:biz:customer", Sequence = 1,
        }.ToStoredMessage();

        Assert.Equal(["https://www.tiktok.com/embed/1"], message.AttachmentUrls);
    }

    // --- Download links and file names for the AI team ---

    private static readonly DateTime SentAt = new(2026, 11, 23, 13, 21, 53, DateTimeKind.Utc);

    private const string PhotoKey = "customer-media/user-1/wamid.1-0.jpg";

    private static readonly FakeMediaLinkSigner Links = new();

    private static WhatsAppMessageDocument Customer(string type, string? text, List<MessageAttachment>? files = null, MessageReplyTo? replyTo = null) => new()
    {
        MessageId = "whatsapp:wamid.1", Platform = MessagingPlatform.WhatsApp, ExternalAccountId = "456",
        ChannelId = "123", ChannelDisplayName = "+234 901 111 2222", From = "2348012345678", Timestamp = "0",
        Type = type, TextBody = text, Receiver = "2349011112222", SentAt = SentAt, StoredAt = SentAt.AddSeconds(5),
        ConversationKey = "whatsapp:2349011112222:2348012345678", Sequence = 1, Attachments = files, ReplyTo = replyTo,
    };

    private static MessageAttachment SavedPhoto() => new()
    {
        Kind = MediaKinds.Image, ContentType = "image/jpeg", SizeBytes = 56446, StorageKey = PhotoKey, Status = MediaStatus.Ready,
    };

    [Fact]
    public void ToStoredMessage_APhotoWithACaption_HasItsCaptionAsTextAndALinkToThePhoto()
    {
        StoredMessage message = Customer("image", "Do you have this in black?", [SavedPhoto()]).ToStoredMessage(Links);

        Assert.Equal("Do you have this in black?", message.Text);
        StoredAttachment photo = Assert.Single(message.Attachments!);
        Assert.Equal(FakeMediaLinkSigner.LinkFor(PhotoKey, "image-20261123-132153.jpg"), photo.Url);
        Assert.Equal("image-20261123-132153.jpg", photo.FileName);
        Assert.True(photo.Available);
        Assert.Null(message.ReplyTo);
    }

    [Fact]
    public void ToStoredMessage_APhotoOnly_HasNoTextAndALinkToThePhoto()
    {
        StoredMessage message = Customer("image", null, [SavedPhoto()]).ToStoredMessage(Links);

        Assert.Null(message.Text);
        Assert.Equal(FakeMediaLinkSigner.LinkFor(PhotoKey, "image-20261123-132153.jpg"), Assert.Single(message.Attachments!).Url);
    }

    [Fact]
    public void ToStoredMessage_TextQuotingAPhoto_LinksTheQuotedPhoto()
    {
        MessageReplyTo quote = new()
        {
            ProviderMessageId = "wamid.OURS", MessageId = "outgoing:user-1:abc", Outgoing = true,
            Text = "Here are the bags we have in stock", Type = "image", StorageKey = "business-media/user-1/abc.jpg",
        };

        StoredMessage message = Customer("text", "How much is this one?", replyTo: quote).ToStoredMessage(Links);

        Assert.Null(message.Attachments);
        Assert.Equal(new StoredQuote("outgoing:user-1:abc", true, "Here are the bags we have in stock", "image",
            FakeMediaLinkSigner.LinkFor("business-media/user-1/abc.jpg", "image.jpg")), message.ReplyTo);
    }

    [Fact]
    public void ToStoredMessage_APhotoQuotingText_LinksThePhotoAndNotTheQuote()
    {
        MessageReplyTo quote = new()
        {
            ProviderMessageId = "wamid.OURS", MessageId = "outgoing:user-1:abc", Outgoing = true,
            Text = "Please send a photo of your payment receipt.", Type = "text",
        };

        StoredMessage message = Customer("image", null, [SavedPhoto()], quote).ToStoredMessage(Links);

        Assert.NotNull(Assert.Single(message.Attachments!).Url);
        Assert.Equal(new StoredQuote("outgoing:user-1:abc", true, "Please send a photo of your payment receipt.", "text", null), message.ReplyTo);
    }

    [Fact]
    public void ToStoredMessage_AFileWithItsOwnName_KeepsTheName()
    {
        MessageAttachment document = new()
        {
            Kind = MediaKinds.Document, ContentType = "application/pdf", FileName = "invoice.pdf",
            StorageKey = "customer-media/user-1/wamid.1-0.pdf", Status = MediaStatus.Ready,
        };

        StoredAttachment stored = Assert.Single(Customer("document", null, [document]).ToStoredMessage(Links).Attachments!);

        Assert.Equal("invoice.pdf", stored.FileName);
        Assert.Equal(FakeMediaLinkSigner.LinkFor("customer-media/user-1/wamid.1-0.pdf", "invoice.pdf"), stored.Url);
    }

    [Fact]
    public void ToStoredMessage_SeveralUnnamedFilesTogether_AreNumbered()
    {
        MessageAttachment second = SavedPhoto();
        second.StorageKey = "customer-media/user-1/wamid.1-1.jpg";

        StoredMessage message = Customer("image", null, [SavedPhoto(), second]).ToStoredMessage(Links);

        Assert.Equal(["image-20261123-132153-1.jpg", "image-20261123-132153-2.jpg"], message.Attachments!.Select(a => a.FileName));
    }

    [Theory]
    [InlineData(MediaStatus.Pending)]
    [InlineData(MediaStatus.Failed)]
    [InlineData(MediaStatus.TooLarge)]
    public void ToStoredMessage_AFileWithNoSavedCopy_HasANameButNoUrl(string status)
    {
        MessageAttachment unsaved = new() { Kind = MediaKinds.Image, ContentType = "image/jpeg", Status = status };

        StoredAttachment stored = Assert.Single(Customer("image", null, [unsaved]).ToStoredMessage(Links).Attachments!);

        Assert.Null(stored.Url);
        Assert.Equal("image-20261123-132153", stored.FileName);
        Assert.False(stored.Available);
        Assert.Equal(status, stored.Status);
    }

    // The history API passes no signer: it gives no links, but the same names.
    [Fact]
    public void ToStoredMessage_WithoutASigner_LeavesEveryUrlNull()
    {
        MessageReplyTo quote = new() { ProviderMessageId = "wamid.OLD", Type = "image", StorageKey = PhotoKey };

        StoredMessage message = Customer("image", null, [SavedPhoto()], quote).ToStoredMessage();

        StoredAttachment stored = Assert.Single(message.Attachments!);
        Assert.Null(stored.Url);
        Assert.Equal("image-20261123-132153.jpg", stored.FileName);
        Assert.Null(message.ReplyTo!.Url);
    }

    [Fact]
    public void ToStoredMessage_WhenNoLinkCanBeMade_StillDescribesTheFile()
    {
        StoredMessage message = Customer("image", null, [SavedPhoto()]).ToStoredMessage(new FakeMediaLinkSigner(isConfigured: false));

        StoredAttachment stored = Assert.Single(message.Attachments!);
        Assert.Null(stored.Url);
        Assert.True(stored.Available);
        Assert.Equal("image-20261123-132153.jpg", stored.FileName);
    }

    // A shared post is an address, not a file: it is its own url, as the inbox opens it, and has no name.
    [Fact]
    public void ToStoredMessage_ASharedPost_IsItsOwnUrlWithNoName()
    {
        MessageAttachment post = new() { Kind = MediaKinds.Link, SourceUrl = "https://www.tiktok.com/@shop/video/1" };

        StoredAttachment stored = Assert.Single(Customer("share_post", null, [post]).ToStoredMessage(Links).Attachments!);

        Assert.Equal("https://www.tiktok.com/@shop/video/1", stored.Url);
        Assert.Null(stored.FileName);
        Assert.True(stored.Available);
    }

    [Fact]
    public void MessagePersisted_OnTheWire_PutsUrlRightAfterKind_AndAUrlOnTheQuote()
    {
        MessageReplyTo quote = new() { ProviderMessageId = "wamid.OLD", MessageId = "whatsapp:wamid.OLD", Type = "image", StorageKey = PhotoKey };
        MessagePersisted persisted = new()
        {
            Source = MessagingPlatform.WhatsApp, CorrelationId = "c",
            Data = Customer("image", null, [SavedPhoto()], quote).ToStoredMessage(Links),
        };

        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(persisted, MessageJson.Options));
        JsonElement data = json.RootElement.GetProperty("data");

        Assert.Equal(["kind", "url", "contentType", "fileName", "sizeBytes", "available", "status"],
            data.GetProperty("attachments")[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(["id", "outgoing", "text", "type", "url"], data.GetProperty("replyTo").EnumerateObject().Select(p => p.Name));
        Assert.Equal(FakeMediaLinkSigner.LinkFor(PhotoKey, "image.jpg"), data.GetProperty("replyTo").GetProperty("url").GetString());
    }

    // The API's live push reads events the worker wrote, possibly a version behind: one without url still reads.
    [Fact]
    public void MessagePersisted_WrittenWithoutUrls_StillReads()
    {
        const string older = """
            {"schemaVersion":2,"source":"whatsapp","correlationId":"c","data":{"messageId":"whatsapp:wamid.1",
             "conversationKey":"k","sequence":1,"platform":"whatsapp","outgoing":false,"sender":"1","receiver":"2","type":"image",
             "attachments":[{"kind":"image","contentType":"image/jpeg","fileName":null,"sizeBytes":5,"available":true,"status":"ready"}],
             "persistedAt":"2026-11-23T13:21:58Z","replyTo":{"id":"x","outgoing":true,"text":"hi","type":"text"}}}
            """;

        MessagePersisted read = JsonSerializer.Deserialize<MessagePersisted>(older, MessageJson.Options)!;

        Assert.Null(Assert.Single(read.Data.Attachments!).Url);
        Assert.Equal(new StoredQuote("x", true, "hi", "text"), read.Data.ReplyTo);
    }
}
