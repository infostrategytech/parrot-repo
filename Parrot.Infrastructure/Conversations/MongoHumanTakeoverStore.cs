using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Parrot.Application.Conversations;
using Parrot.Infrastructure.Messaging;

namespace Parrot.Infrastructure.Conversations;

public sealed class MongoHumanTakeoverStore : IHumanTakeoverStore
{
    private readonly IMongoCollection<ConversationFlagDocument> _flags;

    public MongoHumanTakeoverStore(IMongoClient client, IOptions<MongoDbSettings> mongo)
    {
        _flags = client.GetDatabase(mongo.Value.Database).GetCollection<ConversationFlagDocument>("conversation_flags");
    }

    // Sets HumanTookOverAt to now and clears HumanHandedBackAt so a re-takeover after handback works.
    public Task SetAsync(string owner, Guid connection, string sender, CancellationToken ct) =>
        UpsertAsync(owner, connection, sender,
            Builders<ConversationFlagDocument>.Update
                .Set(x => x.HumanTookOverAt, DateTime.UtcNow)
                .Unset(x => x.HumanHandedBackAt),
            ct);

    // Soft delete: records the handback time without touching HumanTookOverAt.
    public Task ClearAsync(string owner, Guid connection, string sender, CancellationToken ct) =>
        UpsertAsync(owner, connection, sender,
            Builders<ConversationFlagDocument>.Update.Set(x => x.HumanHandedBackAt, DateTime.UtcNow),
            ct);

    // Active when a human took over and has not yet handed back.
    public async Task<bool> IsActiveAsync(string owner, Guid connection, string sender, CancellationToken ct)
    {
        ConversationFlagDocument? doc = await _flags
            .Find(x => x.ReaderUserId == owner && x.ConnectionId == connection && x.Sender == sender
                       && x.HumanTookOverAt != null && x.HumanHandedBackAt == null)
            .FirstOrDefaultAsync(ct);
        return doc is not null;
    }

    private async Task UpsertAsync(string owner, Guid connection, string sender,
        UpdateDefinition<ConversationFlagDocument> change, CancellationToken ct)
    {
        FilterDefinition<ConversationFlagDocument> key = Builders<ConversationFlagDocument>.Filter.Where(
            x => x.ReaderUserId == owner && x.ConnectionId == connection && x.Sender == sender);
        UpdateDefinition<ConversationFlagDocument> update = Builders<ConversationFlagDocument>.Update
            .Combine(change, Builders<ConversationFlagDocument>.Update.Set(x => x.UpdatedAt, DateTime.UtcNow));
        try { await _flags.UpdateOneAsync(key, update, new UpdateOptions { IsUpsert = true }, ct); }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            await _flags.UpdateOneAsync(key, update, cancellationToken: ct);
        }
    }
}
