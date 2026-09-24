using Microsoft.Extensions.Options;
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
    }

    public async Task InsertAsync(IncomingMessage message, CancellationToken cancellationToken = default)
    {
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
        string channelId,
        DateTime since,
        CancellationToken cancellationToken = default)
    {
        // ChannelId now lives on the base document, so this no longer needs an untyped field name.
        FilterDefinition<MessageDocument> filter = Builders<MessageDocument>.Filter.And(
            Builders<MessageDocument>.Filter.Eq(x => x.ChannelId, channelId),
            Builders<MessageDocument>.Filter.Gte(x => x.ReceivedAt, since));

        MessageDocument? doc = await _collection
            .Find(filter)
            .SortBy(x => x.ReceivedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return doc is null ? null : new ReceivedMessageSummary(doc.MessageId, doc.From, doc.ReceivedAt);
    }
}
