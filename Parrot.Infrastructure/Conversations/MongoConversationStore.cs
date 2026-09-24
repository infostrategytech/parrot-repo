using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Parrot.Application.Conversations;
using Parrot.Domain.Entities.Messages;
using Parrot.Domain.Exceptions;
using Parrot.Infrastructure.Messaging;

namespace Parrot.Infrastructure.Conversations;

public sealed class MongoConversationStore : IConversationStore
{
    private readonly IMongoCollection<MessageDocument> _messages;
    public MongoConversationStore(IMongoClient client, IOptions<MongoDbSettings> options)
    {
        _messages = client.GetDatabase(options.Value.Database).GetCollection<MessageDocument>(options.Value.Collection);
        _messages.Indexes.CreateOne(new CreateIndexModel<MessageDocument>(
            Builders<MessageDocument>.IndexKeys.Ascending(x => x.MessageId), new CreateIndexOptions { Unique = true }));
        _messages.Indexes.CreateOne(new CreateIndexModel<MessageDocument>(Builders<MessageDocument>.IndexKeys
            .Ascending(x => x.OwnerUserId).Ascending(x => x.ConnectionId).Ascending(x => x.From).Descending(x => x.Id)));
    }

    public async Task<IReadOnlyList<ConversationSummary>> ListAsync(string owner, int skip, int take, CancellationToken ct)
    {
        List<BsonDocument> rows = await _messages.Aggregate()
            .Match(x => x.OwnerUserId == owner && x.ConnectionId != null)
            .SortByDescending(x => x.Id)
            .AppendStage<BsonDocument>(new BsonDocument("$group", new BsonDocument
            {
                { "_id", new BsonDocument { { "connection", "$ConnectionId" }, { "sender", "$From" } } },
                { "latest", new BsonDocument("$first", "$$ROOT") },
                { "name", new BsonDocument("$max", "$ContactName") },
            }))
            .AppendStage<BsonDocument>(new BsonDocument("$sort", new BsonDocument("latest._id", -1)))
            .Skip(skip).Limit(take).ToListAsync(ct);
        return rows.Select(row =>
        {
            MessageDocument d = BsonSerializer.Deserialize<MessageDocument>(row["latest"].AsBsonDocument);
            string name = row["name"].IsString ? row["name"].AsString : d.From;
            return new ConversationSummary(d.ConnectionId!.Value, d.From, name,
                d.TextBody ?? $"[{d.Type} message]", d.ReceivedAt);
        }).ToList();
    }

    public async Task<MessagePage> ReadAsync(string owner, Guid connection, string sender, string? before, CancellationToken ct)
    {
        FilterDefinition<MessageDocument> filter = Builders<MessageDocument>.Filter.Where(x => x.OwnerUserId == owner && x.ConnectionId == connection && x.From == sender);
        if (before is not null)
        {
            if (!ObjectId.TryParse(before, out ObjectId id)) throw new ValidationException("Invalid message cursor.");
            filter &= new BsonDocument("_id", new BsonDocument("$lt", id));
        }
        List<MessageDocument> docs = await _messages.Find(filter).SortByDescending(x => x.Id).Limit(101).ToListAsync(ct);
        List<MessageDocument> page = docs.Take(100).ToList();
        return new MessagePage(page.AsEnumerable().Reverse().Select(ConversationService.ToMessage).ToList(),
            docs.Count > 100 ? page[^1].Id : null);
    }

    public async Task<DateTime?> LastIncomingAsync(string owner, Guid connection, string sender, CancellationToken ct)
    {
        FilterDefinition<MessageDocument> filter = Builders<MessageDocument>.Filter.Where(
            x => x.OwnerUserId == owner && x.ConnectionId == connection && x.From == sender)
            & Builders<MessageDocument>.Filter.Ne(x => x.Outgoing, true);
        // Ordered by ReceivedAt: Timestamp is a provider string whose units differ per platform.
        MessageDocument? doc = await _messages.Find(filter).SortByDescending(x => x.ReceivedAt).FirstOrDefaultAsync(ct);

        // SentAt is absent on documents written before it existed; ReceivedAt is within seconds of it.
        return doc is null ? null : doc.SentAt ?? doc.ReceivedAt;
    }

    public async Task<MessageDocument?> FindRequestAsync(string owner, Guid request, CancellationToken ct) =>
        await _messages.Find(x => x.OwnerUserId == owner && x.MessageId == ConversationService.RequestKey(owner, request)).FirstOrDefaultAsync(ct);

    public async Task<bool> ReserveAsync(WhatsAppMessageDocument message, CancellationToken ct)
    {
        try { await _messages.InsertOneAsync(message, cancellationToken: ct); return true; }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey) { return false; }
    }

    public async Task CompleteAsync(string id, string status, string? providerId, CancellationToken ct) =>
        await _messages.UpdateOneAsync(x => x.MessageId == id,
            Builders<MessageDocument>.Update.Set(x => x.SendStatus, status).Set(x => x.ProviderMessageId, providerId), cancellationToken: ct);
}
