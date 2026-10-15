using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Moq;
using Npgsql;
using Parrot.Application.Conversations;
using Parrot.Application.Integrations;
using Parrot.Application.Messaging;
using Parrot.Application.Models;
using Parrot.Application.Storage;
using Parrot.Domain.Entities;
using Parrot.Domain.Entities.Messages;
using Parrot.Domain.Enums;
using Parrot.Infrastructure.Conversations;
using Parrot.Infrastructure.Data;
using static Test.Parrot.Api.Tests.Services.Integrations.ConnectionMessageReceiptPostgresTests;

namespace Test.Parrot.Api.Tests.Services.Integrations;

// The send worker's dispatcher reads the connection (and so the token) from PostgreSQL, the file from private
// storage, and hands both to the channel. Run against a disposable database: see PARROT_TEST_POSTGRES in
// ConnectionMessageReceiptPostgresTests.
public class OutboundFileDispatcherPostgresTests : IAsyncLifetime
{
    private sealed class Contexts(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(options);
    }

    private const string Key = "business-media/user/0f1c.png";
    private static readonly byte[] Bytes = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

    private readonly string _database = "parrot_test_" + Guid.NewGuid().ToString("N");
    private readonly Mock<IWhatsAppSender> _whatsApp = new();
    private readonly Mock<IFacebookSender> _facebook = new();
    private readonly Mock<IWhatsAppTokenProtector> _whatsAppProtector = new();
    private readonly Mock<IFacebookCredentialProtector> _facebookProtector = new();
    private readonly Mock<IPrivateObjectStorage> _storage = new();
    private DbContextOptions<ApplicationDbContext>? _options;
    private string _owner = "";

    public async Task InitializeAsync()
    {
        string? server = Environment.GetEnvironmentVariable("PARROT_TEST_POSTGRES");
        if (server is null) return;
        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(server) { Database = _database }.ConnectionString).Options;
        await using ApplicationDbContext db = new(_options);
        await db.Database.EnsureCreatedAsync();
        ApplicationUser user = new() { UserName = "owner@example.com", Email = "owner@example.com" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        _owner = user.Id;

        _whatsAppProtector.Setup(p => p.Unprotect("enc")).Returns("wa-token");
        _facebookProtector.Setup(p => p.Unprotect("enc")).Returns("page-token");
        _storage.Setup(s => s.GetAsync(Key, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new StoredObject(new MemoryStream(Bytes), "image/png", Bytes.Length));
    }

    public async Task DisposeAsync()
    {
        if (_options is null) return;
        await using ApplicationDbContext db = new(_options);
        await db.Database.EnsureDeletedAsync();
    }

    private OutboundFileDispatcher Dispatcher() => new(new Contexts(_options!), _whatsApp.Object, _facebook.Object,
        _whatsAppProtector.Object, _facebookProtector.Object, _storage.Object);

    private async Task<WhatsAppConnection> WhatsAppAsync(Action<WhatsAppConnection>? tweak = null)
    {
        WhatsAppConnection connection = new()
        {
            OwnerUserId = _owner, Name = "Shop", VerifyTokenEncrypted = "v", PhoneNumberId = "phone-1", AccessTokenEncrypted = "enc",
            WebhookVerifiedAt = DateTime.UtcNow,
        };
        tweak?.Invoke(connection);
        await using ApplicationDbContext db = new(_options!);
        db.WhatsAppConnections.Add(connection);
        await db.SaveChangesAsync();
        return connection;
    }

    private async Task<FacebookConnection> PageAsync(Action<FacebookConnection>? tweak = null)
    {
        FacebookConnection page = new() { OwnerUserId = _owner, VerifyTokenEncrypted = "v", PageId = "page-1", AccessTokenEncrypted = "enc", WebhookVerifiedAt = DateTime.UtcNow };
        tweak?.Invoke(page);
        await using ApplicationDbContext db = new(_options!);
        db.FacebookConnections.Add(page);
        await db.SaveChangesAsync();
        return page;
    }

    private MessageDocument Reply(Guid connection, MessagingPlatform platform = MessagingPlatform.WhatsApp, string? owner = null) => new WhatsAppMessageDocument
    {
        MessageId = "outgoing:x:1", OwnerUserId = owner ?? _owner, ConnectionId = connection, Platform = platform, ExternalAccountId = "x", ChannelId = "x",
        ChannelDisplayName = "", From = "2348012345678", Timestamp = "1", Type = "image", Outgoing = true, SendStatus = "sending", TextBody = "Menu",
        Attachments = [new MessageAttachment { Kind = "image", ContentType = "image/png", FileName = "menu.png", StorageKey = Key, Status = MediaStatus.Ready }],
    };

    [PostgresFact]
    public async Task A_WhatsApp_reply_is_sent_with_the_connections_token_phone_recipient_caption_and_the_stored_bytes()
    {
        WhatsAppConnection connection = await WhatsAppAsync();
        byte[]? sent = null;
        _whatsApp.Setup(w => w.SendMediaAsync("phone-1", "wa-token", "2348012345678", "image", "image/png", "menu.png", It.IsAny<Stream>(), "Menu", It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string, string, string, Stream, string?, CancellationToken>((_, _, _, _, _, _, body, _, _) =>
            {
                using MemoryStream copy = new();
                body.CopyTo(copy);
                sent = copy.ToArray();
            })
            .ReturnsAsync("wamid.file");

        FileDispatchResult result = await Dispatcher().DispatchAsync(Reply(connection.Id), default);

        Assert.Equal(new FileDispatchResult("wamid.file", null), result);
        Assert.Equal(Bytes, sent);
    }

    [PostgresFact]
    public async Task A_Messenger_reply_is_sent_through_the_page_with_its_token()
    {
        FacebookConnection page = await PageAsync();
        _facebook.Setup(f => f.SendMediaAsync("page-1", "page-token", "2348012345678", "image", "image/png", "menu.png", It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("m_file");

        FileDispatchResult result = await Dispatcher().DispatchAsync(Reply(page.Id, MessagingPlatform.Facebook), default);

        Assert.Equal(new FileDispatchResult("m_file", null), result);
    }

    [PostgresFact]
    public async Task A_refusal_comes_back_as_its_code_and_a_failed_upload_as_the_retryable_one()
    {
        WhatsAppConnection connection = await WhatsAppAsync();
        _whatsApp.SetupSequence(w => w.SendMediaAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new WhatsAppRejectedException("131047"))
            .ThrowsAsync(new WhatsAppRejectedException("upload"));

        FileDispatchResult refused = await Dispatcher().DispatchAsync(Reply(connection.Id), default);
        FileDispatchResult upload = await Dispatcher().DispatchAsync(Reply(connection.Id), default);

        Assert.Equal("131047", refused.RejectedCode);
        Assert.False(refused.IsSafeToRetry);
        Assert.True(upload.IsSafeToRetry);
    }

    [PostgresFact]
    public async Task A_dropped_call_to_the_channel_is_not_swallowed_because_it_means_delivery_is_unknown()
    {
        WhatsAppConnection connection = await WhatsAppAsync();
        _whatsApp.Setup(w => w.SendMediaAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("reset"));

        await Assert.ThrowsAsync<HttpRequestException>(() => Dispatcher().DispatchAsync(Reply(connection.Id), default));
    }

    [PostgresFact]
    public async Task Nothing_is_sent_when_the_connection_is_gone_disabled_or_another_owners()
    {
        WhatsAppConnection disabled = await WhatsAppAsync(c => c.DisabledAt = DateTime.UtcNow);
        WhatsAppConnection live = await WhatsAppAsync();

        Assert.Equal("connection", (await Dispatcher().DispatchAsync(Reply(disabled.Id), default)).RejectedCode);
        Assert.Equal("connection", (await Dispatcher().DispatchAsync(Reply(Guid.NewGuid()), default)).RejectedCode);
        Assert.Equal("connection", (await Dispatcher().DispatchAsync(Reply(live.Id, owner: "someone-else"), default)).RejectedCode);
        _whatsApp.VerifyNoOtherCalls();
    }

    [PostgresFact]
    public async Task Nothing_is_sent_when_the_token_is_missing_or_the_key_ring_cannot_read_it_or_the_file_is_gone()
    {
        WhatsAppConnection none = await WhatsAppAsync(c => c.AccessTokenEncrypted = null);
        WhatsAppConnection unreadable = await WhatsAppAsync(c => c.AccessTokenEncrypted = "rotated");
        _whatsAppProtector.Setup(p => p.Unprotect("rotated")).Throws(new CryptographicException("key ring rotated"));
        WhatsAppConnection live = await WhatsAppAsync();

        Assert.Equal("token", (await Dispatcher().DispatchAsync(Reply(none.Id), default)).RejectedCode);
        Assert.Equal("token", (await Dispatcher().DispatchAsync(Reply(unreadable.Id), default)).RejectedCode);

        _storage.Setup(s => s.GetAsync(Key, It.IsAny<CancellationToken>())).ReturnsAsync((StoredObject?)null);
        Assert.Equal("file", (await Dispatcher().DispatchAsync(Reply(live.Id), default)).RejectedCode);
        _whatsApp.VerifyNoOtherCalls();
    }

    [PostgresFact]
    public async Task A_quoted_WhatsApp_reply_is_sent_quoting_the_message_the_agent_chose()
    {
        WhatsAppConnection connection = await WhatsAppAsync();
        MessageDocument reply = Reply(connection.Id);
        reply.ReplyTo = new MessageReplyTo { ProviderMessageId = "wamid.QUOTED" };
        _whatsApp.Setup(w => w.SendMediaReplyAsync("phone-1", "wa-token", "2348012345678", "image", "image/png", "menu.png", It.IsAny<Stream>(), "Menu", "wamid.QUOTED", It.IsAny<CancellationToken>()))
            .ReturnsAsync("wamid.file");

        FileDispatchResult result = await Dispatcher().DispatchAsync(reply, default);

        Assert.Equal("wamid.file", result.ProviderId);
        _whatsApp.Verify(w => w.SendMediaAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [PostgresFact]
    public async Task A_quoted_Messenger_reply_is_sent_quoting_the_message_the_agent_chose()
    {
        FacebookConnection page = await PageAsync();
        MessageDocument reply = Reply(page.Id, MessagingPlatform.Facebook);
        reply.ReplyTo = new MessageReplyTo { ProviderMessageId = "m_QUOTED" };
        _facebook.Setup(f => f.SendMediaReplyAsync("page-1", "page-token", "2348012345678", "image", "image/png", "menu.png", It.IsAny<Stream>(), "m_QUOTED", It.IsAny<CancellationToken>()))
            .ReturnsAsync("m_file");

        Assert.Equal("m_file", (await Dispatcher().DispatchAsync(reply, default)).ProviderId);
    }
}
