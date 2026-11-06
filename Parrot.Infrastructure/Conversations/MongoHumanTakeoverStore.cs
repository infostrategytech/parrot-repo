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

    // Sets HumanTookOverAt to now, stores the expiry, and clears HumanHandedBackAt so a re-takeover after handback works.
    public Task SetAsync(string owner, Guid connection, string sender, DateTime? controlUntil, CancellationToken ct) =>
        UpsertAsync(owner, connection, sender,
            Builders<ConversationFlagDocument>.Update
                .Set(x => x.HumanTookOverAt, DateTime.UtcNow)
                .Set(x => x.ControlUntil, controlUntil)
                .Unset(x => x.HumanHandedBackAt),
            ct);

    // Soft delete: records the handback time without touching HumanTookOverAt.
    public Task ClearAsync(string owner, Guid connection, string sender, CancellationToken ct) =>
        UpsertAsync(owner, connection, sender,
            Builders<ConversationFlagDocument>.Update.Set(x => x.HumanHandedBackAt, DateTime.UtcNow),
            ct);

    // Active when a human took over, has not handed back, and the timed window has not expired.
    public async Task<bool> IsActiveAsync(string owner, Guid connection, string sender, CancellationToken ct)
    {
        DateTime now = DateTime.UtcNow;
        ConversationFlagDocument? doc = await _flags
            .Find(x => x.ReaderUserId == owner && x.ConnectionId == connection && x.Sender == sender
                       && x.HumanTookOverAt != null && x.HumanHandedBackAt == null)
            .FirstOrDefaultAsync(ct);
        return doc is not null && (doc.ControlUntil == null || doc.ControlUntil > now);
    }

    // Returns active takeovers (not expired, not handed back) for a batch of conversations.
    public async Task<IReadOnlyDictionary<(Guid Connection, string Sender), DateTime?>> GetActiveAsync(
        string owner, IReadOnlyList<(Guid Connection, string Sender)> conversations, CancellationToken ct)
    {
        if (conversations.Count == 0) return new Dictionary<(Guid, string), DateTime?>();

        DateTime now = DateTime.UtcNow;
        List<Guid> connectionIds = [.. conversations.Select(c => c.Connection).Distinct()];

        List<ConversationFlagDocument> docs = await _flags
            .Find(x => x.ReaderUserId == owner && connectionIds.Contains(x.ConnectionId)
                       && x.HumanTookOverAt != null && x.HumanHandedBackAt == null)
            .ToListAsync(ct);

        HashSet<(Guid, string)> keys = [.. conversations];
        return docs
            .Where(d => keys.Contains((d.ConnectionId, d.Sender)) && (d.ControlUntil == null || d.ControlUntil > now))
            .ToDictionary(d => (d.ConnectionId, d.Sender), d => d.ControlUntil);
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
