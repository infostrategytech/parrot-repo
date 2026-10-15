using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Messaging;
using Parrot.Domain.Entities.Messages;
using Parrot.Domain.Enums;
using Parrot.Infrastructure.Messaging;

namespace Test.Parrot.Api.Tests.Services.Messaging;

// The compare-and-set that settles a file, on real MongoDB: the in-memory fakes cannot show that the update
// reaches the right element of the attachments array, or that a second writer is refused.
public class MediaCompletionMongoTests : IAsyncLifetime
{
    private readonly string _database = "parrot_test_" + Guid.NewGuid().ToString("N");
    private MongoClient? _client;

    public Task InitializeAsync()
    {
        string? connection = Environment.GetEnvironmentVariable("PARROT_TEST_MONGO");
        if (connection is not null) _client = new MongoClient(connection);
        MessageSerializationRegistry.Register();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_client is not null) await _client.DropDatabaseAsync(_database);
    }

    private IMongoCollection<MessageDocument> Messages => _client!.GetDatabase(_database).GetCollection<MessageDocument>("messages");

    private MongoStoredMessageStore Store() =>
        new(_client!, Options.Create(new MongoDbSettings { Database = _database, Collection = "messages" }));

    private static MessageDocument TwoPendingFiles() => new WhatsAppMessageDocument
    {
        MessageId = "whatsapp:wamid.M", OwnerUserId = "user-1", Platform = MessagingPlatform.WhatsApp, ExternalAccountId = "waba",
        ChannelId = "phone", ChannelDisplayName = "", From = "2348012345678", Timestamp = "1", Type = "image",
        Attachments =
        [
            new MessageAttachment { Kind = "image", Status = MediaStatus.Pending, ProviderMediaId = "a", SourceUrl = "https://x/a" },
            new MessageAttachment { Kind = "image", Status = MediaStatus.Pending, ProviderMediaId = "b", SourceUrl = "https://x/b" },
        ],
    };

    private static IncomingMedia Ready(string key) => new()
    { Kind = "gif", Status = MediaStatus.Ready, StorageKey = key, ContentType = "image/gif", SizeBytes = 7 };

    [Fact]
    public async Task Settling_one_file_changes_only_that_element_and_a_second_settle_is_refused()
    {
        if (_client is null) return;
        await Messages.InsertOneAsync(TwoPendingFiles());
        MongoStoredMessageStore store = Store();

        Assert.True(await store.CompleteMediaAsync("whatsapp:wamid.M", 1, Ready("k1")));
        Assert.False(await store.CompleteMediaAsync("whatsapp:wamid.M", 1, new IncomingMedia { Kind = "image", Status = MediaStatus.Failed, FailureReason = "error" }));

        MessageDocument stored = (await store.FindAsync("whatsapp:wamid.M"))!;
        MessageAttachment first = stored.Attachments![0];
        MessageAttachment second = stored.Attachments![1];
        Assert.Equal(MediaStatus.Pending, first.Status);
        Assert.Equal("https://x/a", first.SourceUrl);
        Assert.Equal(MediaStatus.Ready, second.Status);
        Assert.Equal("k1", second.StorageKey);
        Assert.Equal("gif", second.Kind);
        Assert.Equal("image/gif", second.ContentType);
        Assert.Equal(7, second.SizeBytes);
        Assert.Null(second.SourceUrl);
        Assert.Null(second.FailureReason);
    }

    [Fact]
    public async Task A_failure_keeps_its_reason_and_a_missing_message_or_position_changes_nothing()
    {
        if (_client is null) return;
        await Messages.InsertOneAsync(TwoPendingFiles());
        MongoStoredMessageStore store = Store();

        Assert.True(await store.CompleteMediaAsync("whatsapp:wamid.M", 0, new IncomingMedia { Kind = "image", Status = MediaStatus.Failed, FailureReason = MediaFailure.Refused }));
        Assert.False(await store.CompleteMediaAsync("whatsapp:wamid.M", 5, Ready("k")));
        Assert.False(await store.CompleteMediaAsync("whatsapp:wamid.NONE", 0, Ready("k")));

        MessageAttachment first = (await store.FindAsync("whatsapp:wamid.M"))!.Attachments![0];
        Assert.Equal(MediaStatus.Failed, first.Status);
        Assert.Equal(MediaFailure.Refused, first.FailureReason);
        Assert.Null(first.StorageKey);
    }
}
