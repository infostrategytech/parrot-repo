using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Parrot.Application.DTOs.Integrations;
using Parrot.Application.Integrations;
using Parrot.Domain.Entities.Messages;

namespace Parrot.Infrastructure.Messaging;

public sealed class MongoMessageRepository : IMessageRepository
{
    private readonly IMongoCollection<MessageDocument> _collection;
    private readonly IMessageDocumentMapperFactory _mappers;

    public MongoMessageRepository(
        IMongoClient mongoClient,
        IOptions<MongoDbSettings> settings,
        IMessageDocumentMapperFactory mappers)
    {
        _mappers = mappers;
        IMongoDatabase db = mongoClient.GetDatabase(settings.Value.Database);
        _collection = db.GetCollection<MessageDocument>(settings.Value.Collection);

        IndexKeysDefinition<MessageDocument> indexKeys =
            Builders<MessageDocument>.IndexKeys.Ascending(x => x.MessageId);
        _collection.Indexes.CreateOne(new CreateIndexModel<MessageDocument>(
            indexKeys, new CreateIndexOptions { Unique = true }));

        // One message per position in a conversation. Partial, so documents
        // written before sequences existed (which have neither field) are left alone.
        _collection.Indexes.CreateOne(new CreateIndexModel<MessageDocument>(
            Builders<MessageDocument>.IndexKeys.Ascending(x => x.ConversationKey).Ascending(x => x.Sequence),
            new CreateIndexOptions<MessageDocument>
            {
                Unique = true,
                Name = "ConversationKey_Sequence_unique",
                PartialFilterExpression = Builders<MessageDocument>.Filter.Type(x => x.Sequence, BsonType.Int64),
            }));
    }

    public async Task InsertAsync(IncomingMessage message, CancellationToken cancellationToken = default)
    {
        // Replaces the switch that threw for any non-WhatsApp platform, stopping the shared consumer.
        MessageDocument doc = _mappers.ToDocument(message);

        try
        {
            await _collection.InsertOneAsync(doc, cancellationToken: cancellationToken);
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            MessageDocument existing = await _collection.Find(d => d.MessageId == message.MessageId)
                .FirstOrDefaultAsync(cancellationToken);
            if (existing is null || existing.ConnectionId != message.ConnectionId || existing.OwnerUserId != message.OwnerUserId)
            {
                throw new InvalidOperationException("Message ID already belongs to another connection.", ex);
            }
        }
    }

    public async Task<ReceivedMessageSummary?> FindFirstAsync(
        Guid connectionId,
        string ownerUserId,
        DateTime since,
        CancellationToken cancellationToken = default)
    {
        // Parrot's connection id, not Meta's ChannelId (Page or phone number id): the setup
        // screens only know the former, so matching on the latter never found anything.
        // Replies sent during the test are not "a message arrived".
        FilterDefinition<MessageDocument> filter = Builders<MessageDocument>.Filter.And(
            Builders<MessageDocument>.Filter.Eq(x => x.OwnerUserId, ownerUserId),
            Builders<MessageDocument>.Filter.Eq(x => x.ConnectionId, connectionId),
            Builders<MessageDocument>.Filter.Ne(x => x.Outgoing, true),
            Builders<MessageDocument>.Filter.Gte(x => x.ReceivedAt, since));

        MessageDocument? doc = await _collection
            .Find(filter)
            .SortBy(x => x.ReceivedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return doc is null ? null : new ReceivedMessageSummary(doc.MessageId, doc.From, doc.ReceivedAt);
    }
}
