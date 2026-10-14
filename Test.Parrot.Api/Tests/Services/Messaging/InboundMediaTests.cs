using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Parrot.Api.Media;
using Parrot.Application.Conversations;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;
using Parrot.Application.Storage;
using Parrot.Domain.Entities.Messages;
using Parrot.Domain.Enums;
using Parrot.Infrastructure.Conversations;

namespace Test.Parrot.Api.Tests.Services.Messaging;

public class InboundMediaTests
{
    private static readonly InboundMessageDecoders Decoders = new([new WhatsAppInboundDecoder(), new FacebookInboundDecoder(), new InstagramInboundDecoder()]);

    private static string WhatsApp(string message) => $$$"""
        {"object":"whatsapp_business_account","entry":[{"id":"waba-1","changes":[{"field":"messages","value":{
          "messaging_product":"whatsapp",
          "metadata":{"display_phone_number":"+234 901 111 2222","phone_number_id":"phone-1"},
          "contacts":[{"wa_id":"2348012345678","profile":{"name":"Ada"}}],
          "messages":[{{{message}}}]}}]}]}
        """;

    private static string Meta(string attachment) => $$$"""
        {"object":"page","entry":[{"id":"page-1","time":1790589123000,"messaging":[
          {"sender":{"id":"psid-1"},"recipient":{"id":"page-1"},"timestamp":1790589123500,
           "message":{"mid":"m.1","attachments":[{{{attachment}}}]}}]}]}
        """;

    private static InboundMessage Inbound(MessagingPlatform source, string id, string data) => new()
    {
        MessageId = id,
        Source = source,
        Sender = "2348012345678",
        Receiver = "2349011112222",
        ConversationKey = "whatsapp:2348012345678:2349011112222",
        Data = data,
        ReceivedAt = new DateTime(2026, 10, 2, 9, 30, 1, DateTimeKind.Utc),
        CorrelationId = "487b7279745d4c41675818ffee460c54",
        ConnectionId = Guid.Parse("6f1c2d3e-4a5b-4c6d-8e7f-0a1b2c3d4e5f"),
        OwnerUserId = "user-1",
    };

    private static IncomingMedia OnlyMedia(IncomingMessage message) => Assert.Single(message.Media!);

    [Fact]
    public void A_WhatsApp_image_keeps_its_media_id_and_its_caption_becomes_the_text()
    {
        IncomingMessage message = Decoders.Decode(Inbound(MessagingPlatform.WhatsApp, "whatsapp:wamid.I",
            WhatsApp("""{"from":"2348012345678","id":"wamid.I","timestamp":"1790589123","type":"image","image":{"id":"media-1","mime_type":"image/jpeg","caption":"My receipt"}}""")));

        IncomingMedia media = OnlyMedia(message);
        Assert.Equal(MediaKinds.Image, media.Kind);
        Assert.Equal("media-1", media.ProviderMediaId);
        Assert.Equal("image/jpeg", media.ContentType);
        Assert.Equal("My receipt", message.TextBody);
    }

    [Theory]
    [InlineData("""{"from":"2348012345678","id":"wamid.S","timestamp":"1790589123","type":"sticker","sticker":{"id":"m","mime_type":"image/webp","animated":false}}""", MediaKinds.Sticker)]
    [InlineData("""{"from":"2348012345678","id":"wamid.S","timestamp":"1790589123","type":"audio","audio":{"id":"m","mime_type":"audio/ogg; codecs=opus","voice":true}}""", MediaKinds.Voice)]
    [InlineData("""{"from":"2348012345678","id":"wamid.S","timestamp":"1790589123","type":"audio","audio":{"id":"m","mime_type":"audio/mpeg"}}""", MediaKinds.Audio)]
    [InlineData("""{"from":"2348012345678","id":"wamid.S","timestamp":"1790589123","type":"video","video":{"id":"m","mime_type":"video/mp4"}}""", MediaKinds.Video)]
    [InlineData("""{"from":"2348012345678","id":"wamid.S","timestamp":"1790589123","type":"image","image":{"id":"m","mime_type":"image/gif"}}""", MediaKinds.Gif)]
    [InlineData("""{"from":"2348012345678","id":"wamid.S","timestamp":"1790589123","type":"document","document":{"id":"m","mime_type":"application/pdf","filename":"invoice.pdf"}}""", MediaKinds.Document)]
    public void Each_WhatsApp_media_type_has_its_own_kind(string message, string kind) =>
        Assert.Equal(kind, OnlyMedia(Decoders.Decode(Inbound(MessagingPlatform.WhatsApp, "whatsapp:wamid.S", WhatsApp(message)))).Kind);

    [Fact]
    public void A_WhatsApp_document_keeps_the_file_name_the_customer_sent() =>
        Assert.Equal("invoice.pdf", OnlyMedia(Decoders.Decode(Inbound(MessagingPlatform.WhatsApp, "whatsapp:wamid.D",
            WhatsApp("""{"from":"2348012345678","id":"wamid.D","timestamp":"1790589123","type":"document","document":{"id":"m","filename":"invoice.pdf"}}""")))).FileName);

    [Fact]
    public void A_media_message_without_an_id_has_nothing_to_download() =>
        Assert.Null(Decoders.Decode(Inbound(MessagingPlatform.WhatsApp, "whatsapp:wamid.N",
            WhatsApp("""{"from":"2348012345678","id":"wamid.N","timestamp":"1790589123","type":"image","image":{}}"""))).Media);

    [Theory]
    [InlineData("""{"type":"image","payload":{"url":"https://scontent.fbcdn.net/a.jpg","sticker_id":369239263222822}}""", MediaKinds.Sticker)]
    [InlineData("""{"type":"image","payload":{"url":"https://scontent.fbcdn.net/a.jpg"}}""", MediaKinds.Image)]
    [InlineData("""{"type":"file","payload":{"url":"https://cdn.fbsbx.com/a.pdf"}}""", MediaKinds.Document)]
    [InlineData("""{"type":"audio","payload":{"url":"https://cdn.fbsbx.com/a.mp4"}}""", MediaKinds.Audio)]
    [InlineData("""{"type":"video","payload":{"url":"https://cdn.fbsbx.com/a.mp4"}}""", MediaKinds.Video)]
    [InlineData("""{"type":"share","payload":{"url":"https://www.instagram.com/p/abc"}}""", MediaKinds.Link)]
    public void Messenger_and_Instagram_attachments_are_classified(string attachment, string kind)
    {
        foreach (MessagingPlatform platform in new[] { MessagingPlatform.Facebook, MessagingPlatform.Instagram })
        {
            string source = platform == MessagingPlatform.Facebook ? "facebook" : "instagram";
            Assert.Equal(kind, OnlyMedia(Decoders.Decode(Inbound(platform, $"{source}:m.1", Meta(attachment)))).Kind);
        }
    }

    [Fact]
    public void An_attachment_without_an_address_is_ignored() =>
        Assert.Null(Decoders.Decode(Inbound(MessagingPlatform.Facebook, "facebook:m.1", Meta("""{"type":"image","payload":{}}"""))).Media);

    [Fact]
    public void A_message_that_is_only_a_file_is_listed_by_what_it_is()
    {
        MessageDocument document = new WhatsAppMessageDocumentMapper().ToDocument(new IncomingMessage
        {
            MessageId = "whatsapp:wamid.P", Platform = MessagingPlatform.WhatsApp, ExternalAccountId = "waba", From = "2348012345678",
            Timestamp = "1", Type = "image", ChannelId = "phone-1", ChannelDisplayName = "",
            Media = [new IncomingMedia { Kind = MediaKinds.Image, StorageKey = "customer-media/u/a-0.jpg", ContentType = "image/jpeg" }],
        });
        document.ConversationKey = "k";
        document.Sequence = 1;

        ConversationMessage read = ConversationService.ToMessage(document);
        Assert.Equal("", read.Text);
        Assert.Equal("customer-media/u/a-0.jpg", Assert.Single(read.Attachments!).StorageKey);

        StoredAttachment pushed = Assert.Single(document.ToStoredMessage().Attachments!);
        Assert.True(pushed.Available);
        Assert.Equal("Photo", MediaKinds.PreviewLabel(pushed.Kind));
    }

    [Fact]
    public void A_file_that_was_not_saved_is_pushed_as_unavailable()
    {
        MessageDocument document = new WhatsAppMessageDocumentMapper().ToDocument(new IncomingMessage
        {
            MessageId = "whatsapp:wamid.P", Platform = MessagingPlatform.WhatsApp, ExternalAccountId = "waba", From = "2348012345678",
            Timestamp = "1", Type = "document", ChannelId = "phone-1", ChannelDisplayName = "",
            Media = [new IncomingMedia { Kind = MediaKinds.Document, ProviderMediaId = "m" }],
        });
        document.ConversationKey = "k";
        document.Sequence = 1;

        Assert.False(Assert.Single(document.ToStoredMessage().Attachments!).Available);
    }

    [Fact]
    public void A_signed_address_opens_only_what_it_was_made_for_and_not_when_altered()
    {
        MediaLinks links = new(new EphemeralDataProtectionProvider());
        MessageMedia signed = links.Sign(new MessageMedia("image", "image/jpeg", "a.jpg", 10, null) { StorageKey = "customer-media/u/a-0.jpg" });

        string token = signed.Url!["api/media/".Length..];
        Assert.True(links.TryRead(token, out string key, out string? name));
        Assert.Equal("customer-media/u/a-0.jpg", key);
        Assert.Equal("a.jpg", name);
        Assert.False(links.TryRead(token[..^2] + "AA", out _, out _));
        Assert.False(links.TryRead("nonsense", out _, out _));
        Assert.False(new MediaLinks(new EphemeralDataProtectionProvider()).TryRead(token, out _, out _));
    }

    [Fact]
    public void A_file_with_no_saved_copy_gets_no_address() =>
        Assert.Null(new MediaLinks(new EphemeralDataProtectionProvider()).Sign(new MessageMedia("document", null, null, null, null)).Url);

    // ---- the downloader ----------------------------------------------------------------

    private sealed class FakePrivateStorage : IPrivateObjectStorage
    {
        public Dictionary<string, (byte[] Bytes, string ContentType)> Objects { get; } = new();

        public async Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            using MemoryStream copy = new();
            await content.CopyToAsync(copy, cancellationToken);
            Objects[key] = (copy.ToArray(), contentType);
        }

        public Task<StoredObject?> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<StoredObject?>(null);
    }

    private sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : IHttpClientFactory
    {
        public List<Uri> Requests { get; } = new();

        public HttpClient CreateClient(string name) => new(new Handler(this, respond));

        private sealed class Handler(FakeHttp owner, Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Requests.Add(request.RequestUri!);
                return Task.FromResult(respond(request));
            }
        }
    }

    private static MetaMediaDownloader Downloader(FakeHttp http, FakePrivateStorage storage, long maxBytes = 1024) => new(
        http, null!, null!, storage, Options.Create(new WhatsAppSettings()), Options.Create(new StorageSettings { MaxInboundMediaBytes = maxBytes }),
        NullLogger<MetaMediaDownloader>.Instance);

    private static IncomingMessage Facebook(params IncomingMedia[] media) => new()
    {
        MessageId = "facebook:m.1", Platform = MessagingPlatform.Facebook, ExternalAccountId = "page-1", From = "psid-1", Timestamp = "1",
        Type = "image", ChannelId = "page-1", ChannelDisplayName = "", OwnerUserId = "user-1", Media = media,
    };

    [Fact]
    public async Task A_Messenger_picture_is_copied_to_private_storage_and_its_expiring_link_dropped()
    {
        FakePrivateStorage storage = new();
        FakeHttp http = new(_ => new HttpResponseMessage { Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new("image/gif") } } });

        IncomingMedia saved = Assert.Single(await Downloader(http, storage).SaveAsync(
            Facebook(new IncomingMedia { Kind = MediaKinds.Image, SourceUrl = "https://scontent.xx.fbcdn.net/a.gif?sig=1" }), default));

        Assert.Equal(MediaKinds.Gif, saved.Kind);
        Assert.Equal(3, saved.SizeBytes);
        Assert.Null(saved.SourceUrl);
        Assert.StartsWith("customer-media/user-1/", saved.StorageKey);
        Assert.EndsWith(".gif", saved.StorageKey);
        Assert.Equal([1, 2, 3], storage.Objects[saved.StorageKey!].Bytes);
    }

    [Theory]
    [InlineData("http://scontent.xx.fbcdn.net/a.jpg")]
    [InlineData("https://evil.example/a.jpg")]
    [InlineData("https://fbcdn.net.evil.example/a.jpg")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    public async Task An_address_that_is_not_Metas_is_never_fetched(string url)
    {
        FakePrivateStorage storage = new();
        FakeHttp http = new(_ => new HttpResponseMessage());

        IncomingMedia saved = Assert.Single(await Downloader(http, storage).SaveAsync(
            Facebook(new IncomingMedia { Kind = MediaKinds.Image, SourceUrl = url }), default));

        Assert.Empty(http.Requests);
        Assert.Null(saved.StorageKey);
        Assert.Empty(storage.Objects);
    }

    [Fact]
    public async Task A_file_over_the_limit_is_not_kept_but_the_message_still_goes_through()
    {
        FakePrivateStorage storage = new();
        FakeHttp http = new(_ => new HttpResponseMessage { Content = new ByteArrayContent(new byte[2048]) });

        IncomingMedia saved = Assert.Single(await Downloader(http, storage, maxBytes: 1024).SaveAsync(
            Facebook(new IncomingMedia { Kind = MediaKinds.Document, SourceUrl = "https://cdn.fbsbx.com/a.pdf" }), default));

        Assert.Null(saved.StorageKey);
        Assert.Empty(storage.Objects);
    }

    [Fact]
    public async Task A_refused_download_leaves_the_file_unsaved_without_failing()
    {
        FakePrivateStorage storage = new();
        FakeHttp http = new(_ => new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden));

        IncomingMedia saved = Assert.Single(await Downloader(http, storage).SaveAsync(
            Facebook(new IncomingMedia { Kind = MediaKinds.Image, SourceUrl = "https://scontent.xx.fbcdn.net/a.jpg" }), default));

        Assert.Null(saved.StorageKey);
    }

    [Fact]
    public async Task A_shared_post_is_a_link_and_is_not_downloaded()
    {
        FakeHttp http = new(_ => new HttpResponseMessage());

        IncomingMedia saved = Assert.Single(await Downloader(http, new FakePrivateStorage()).SaveAsync(
            Facebook(new IncomingMedia { Kind = MediaKinds.Link, SourceUrl = "https://www.instagram.com/p/abc" }), default));

        Assert.Empty(http.Requests);
        Assert.Equal("https://www.instagram.com/p/abc", saved.SourceUrl);
    }
}
