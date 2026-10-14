using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Microsoft.Extensions.Options;
using Parrot.Application.Conversations;
using Parrot.Infrastructure.Messaging;

namespace Parrot.Infrastructure.Conversations;

public sealed class MongoConversationFlagStore : IConversationFlagStore
{
    private readonly IMongoCollection<ConversationFlagDocument> _flags;

    public MongoConversationFlagStore(IMongoClient client, IOptions<MongoDbSettings> mongo)
    {
        _flags = client.GetDatabase(mongo.Value.Database).GetCollection<ConversationFlagDocument>("conversation_flags");
        _flags.Indexes.CreateOne(new CreateIndexModel<ConversationFlagDocument>(
            Builders<ConversationFlagDocument>.IndexKeys
                .Ascending(x => x.ReaderUserId).Ascending(x => x.ConnectionId).Ascending(x => x.Sender),
            new CreateIndexOptions { Unique = true }));
    }

    public async Task<IReadOnlyDictionary<(Guid Connection, string Sender), ConversationFlags>> ListAsync(string reader, CancellationToken ct)
    {
        List<ConversationFlagDocument> rows = await _flags
            .Find(x => x.ReaderUserId == reader && (x.PinnedAt != null || x.MarkedUnread)).ToListAsync(ct);
        return rows.ToDictionary(x => (x.ConnectionId, x.Sender), x => new ConversationFlags(x.PinnedAt is not null, x.PinnedAt, x.MarkedUnread));
    }

    // Pinning again keeps the first time, so a repeated request does not reorder the pins.
    public Task SetPinnedAsync(string reader, Guid connection, string sender, bool pinned, CancellationToken ct) =>
        UpsertAsync(reader, connection, sender, pinned
            ? Builders<ConversationFlagDocument>.Update.Min(x => x.PinnedAt, DateTime.UtcNow)
            : Builders<ConversationFlagDocument>.Update.Set(x => x.PinnedAt, null), ct);

    public Task SetMarkedUnreadAsync(string reader, Guid connection, string sender, bool markedUnread, CancellationToken ct) =>
        UpsertAsync(reader, connection, sender, Builders<ConversationFlagDocument>.Update.Set(x => x.MarkedUnread, markedUnread), ct);

    private async Task UpsertAsync(string reader, Guid connection, string sender, UpdateDefinition<ConversationFlagDocument> change, CancellationToken ct)
    {
        FilterDefinition<ConversationFlagDocument> key = Builders<ConversationFlagDocument>.Filter.Where(
            x => x.ReaderUserId == reader && x.ConnectionId == connection && x.Sender == sender);
        UpdateDefinition<ConversationFlagDocument> update = Builders<ConversationFlagDocument>.Update
            .Combine(change, Builders<ConversationFlagDocument>.Update.Set(x => x.UpdatedAt, DateTime.UtcNow));
        try { await _flags.UpdateOneAsync(key, update, new UpdateOptions { IsUpsert = true }, ct); }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // Two first requests raced to create the row; the second now updates it.
            await _flags.UpdateOneAsync(key, update, cancellationToken: ct);
        }
    }
}

[BsonIgnoreExtraElements]
internal sealed class ConversationFlagDocument
{
    [BsonId]
    public ObjectId Id { get; set; }
    public string ReaderUserId { get; set; } = "";
    [BsonRepresentation(BsonType.String)]
    public Guid ConnectionId { get; set; }
    public string Sender { get; set; } = "";
    public DateTime? PinnedAt { get; set; }
    public bool MarkedUnread { get; set; }
    public DateTime UpdatedAt { get; set; }
}
