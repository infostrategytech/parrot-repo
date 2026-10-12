using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Parrot.Application.Integrations;
using Parrot.Domain.Entities.Messages;
using Parrot.Domain.Enums;
using Parrot.Infrastructure.Messaging;
using static Test.Parrot.Api.Tests.Services.MongoConversationTests;

namespace Test.Parrot.Api.Tests.Services.Messaging;

public class ConversationSequenceTests : IAsyncLifetime
{
    private const string Ada = "whatsapp:2348012345678:2349011112222";
    private const string Tunde = "whatsapp:2348033334444:2349011112222";

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

    private IOptions<MongoDbSettings> Settings => Options.Create(new MongoDbSettings { Database = _database, Collection = "messages" });

    private MongoConversationSequence Sequence() => new(_client!, Settings);

    [MongoFact]
    public async Task TheFirstMessageOfAConversationIsOne_ThenItCountsUp()
    {
        MongoConversationSequence sequence = Sequence();

        Assert.Equal(1, await sequence.NextAsync(Ada));
        Assert.Equal(2, await sequence.NextAsync(Ada));
        Assert.Equal(3, await sequence.NextAsync(Ada));
    }

    [MongoFact]
    public async Task EachConversationHasItsOwnCounter()
    {
        MongoConversationSequence sequence = Sequence();
        await sequence.NextAsync(Ada);
        await sequence.NextAsync(Ada);

        Assert.Equal(1, await sequence.NextAsync(Tunde));
        Assert.Equal(3, await sequence.NextAsync(Ada));
    }

    // $inc is atomic: callers racing on one conversation never share a number.
    [MongoFact]
    public async Task ConcurrentCallersNeverGetTheSameNumber()
    {
        MongoConversationSequence sequence = Sequence();

        long[] numbers = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => sequence.NextAsync(Ada)));

        Assert.Equal(Enumerable.Range(1, 50).Select(n => (long)n), numbers.Order());
    }

    private static MessageDocument Document(string messageId, string? key, long? sequence) => new WhatsAppMessageDocument
    {
        MessageId = messageId, Platform = MessagingPlatform.WhatsApp, ExternalAccountId = "waba", From = "2348012345678",
        Timestamp = "1790589123", Type = "text", ChannelId = "phone", ChannelDisplayName = "+234 901 111 2222",
        ConversationKey = key, Sequence = sequence,
    };

    private IMongoCollection<MessageDocument> Messages()
    {
        // The repository creates the indexes.
        _ = new MongoMessageRepository(_client!, Settings, new MessageDocumentMapperFactory(
            [new WhatsAppMessageDocumentMapper(), new FacebookMessageDocumentMapper(), new TikTokMessageDocumentMapper(), new InstagramMessageDocumentMapper()]));
        return _client!.GetDatabase(_database).GetCollection<MessageDocument>("messages");
    }

    [MongoFact]
    public async Task TwoMessagesCannotTakeTheSamePositionInAConversation()
    {
        IMongoCollection<MessageDocument> messages = Messages();
        await messages.InsertOneAsync(Document("whatsapp:wamid.1", Ada, 7));

        MongoWriteException ex = await Assert.ThrowsAsync<MongoWriteException>(
            () => messages.InsertOneAsync(Document("whatsapp:wamid.2", Ada, 7)));
        Assert.Equal(ServerErrorCategory.DuplicateKey, ex.WriteError.Category);
    }

    [MongoFact]
    public async Task TheSamePositionInDifferentConversationsIsFine()
    {
        IMongoCollection<MessageDocument> messages = Messages();

        await messages.InsertOneAsync(Document("whatsapp:wamid.1", Ada, 1));
        await messages.InsertOneAsync(Document("whatsapp:wamid.2", Tunde, 1));
    }

    // Documents stored before sequences existed, and outgoing replies until
    // they get one, have neither field and must not collide with each other.
    [MongoFact]
    public async Task DocumentsWithoutASequenceAreNotConstrained()
    {
        IMongoCollection<MessageDocument> messages = Messages();

        await messages.InsertOneAsync(Document("wamid.old-1", null, null));
        await messages.InsertOneAsync(Document("wamid.old-2", null, null));

        Assert.Equal(2, await messages.CountDocumentsAsync(FilterDefinition<MessageDocument>.Empty));
    }
}
