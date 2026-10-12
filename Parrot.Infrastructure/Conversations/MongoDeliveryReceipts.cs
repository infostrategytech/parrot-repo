using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Parrot.Application.Conversations;
using Parrot.Domain.Entities.Messages;
using Parrot.Infrastructure.Messaging;

namespace Parrot.Infrastructure.Conversations;

// Used on the webhook path, so it does nothing in its constructor: the webhook must not depend on
// MongoDB being up, and the caller bounds each call's time. The ProviderMessageId index it looks
// replies up by is created by MongoConversationStore.
public sealed class MongoDeliveryReceipts(IMongoClient client, IOptions<MongoDbSettings> options,
    ILogger<MongoDeliveryReceipts> logger) : IDeliveryReceipts
{
    // A receipt that beat its reply's stored channel id waits this long. Strays expire.
    private static readonly TimeSpan ParkedLifetime = TimeSpan.FromHours(24);

    private readonly IMongoDatabase _database = client.GetDatabase(options.Value.Database);
    private readonly string _collection = options.Value.Collection;
    private IMongoCollection<MessageDocument>? _messages;
    private IMongoCollection<BsonDocument>? _parked;
    private volatile bool _parkedIndexed;

    private IMongoCollection<MessageDocument> Messages => _messages ??= _database.GetCollection<MessageDocument>(_collection);
    private IMongoCollection<BsonDocument> Parked => _parked ??= _database.GetCollection<BsonDocument>("delivery_receipts_parked");

    public async Task RecordAsync(Guid connection, string providerMessageId, DeliveryState state, DateTime at, CancellationToken ct)
    {
        try
        {
            if (!await ApplyAsync(connection, providerMessageId, state, at, ct))
                await ParkAsync(connection, providerMessageId, state, at, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "receipt.record_failed State={State} ConnectionId={ConnectionId}", state, connection);
        }
    }

    public async Task ApplyParkedAsync(Guid connection, string providerMessageId, CancellationToken ct)
    {
        try
        {
            FilterDefinition<BsonDocument> key = ParkedKey(connection, providerMessageId);
            BsonDocument? parked = await Parked.Find(key).FirstOrDefaultAsync(ct);
            if (parked is null) return;

            if (parked.TryGetValue("DeliveredAt", out BsonValue delivered) && delivered.IsValidDateTime)
                await ApplyAsync(connection, providerMessageId, DeliveryState.Delivered, delivered.ToUniversalTime(), ct);
            if (parked.TryGetValue("ReadAt", out BsonValue read) && read.IsValidDateTime)
                await ApplyAsync(connection, providerMessageId, DeliveryState.Read, read.ToUniversalTime(), ct);
            if (parked.TryGetValue("Failed", out BsonValue failed) && failed.IsBoolean && failed.AsBoolean)
                await ApplyAsync(connection, providerMessageId, DeliveryState.Failed, DateTime.UtcNow, ct);
            await Parked.DeleteOneAsync(key, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The receipt stays parked until it expires; the reply just keeps its send status.
            logger.LogWarning(ex, "receipt.apply_parked_failed ConnectionId={ConnectionId}", connection);
        }
    }

    public async Task RecordUpToAsync(Guid connection, string customer, DeliveryState state, DateTime at, DateTime upTo, CancellationToken ct)
    {
        if (state == DeliveryState.Failed) throw new ArgumentOutOfRangeException(nameof(state), state, "A watermark cannot report a failure.");
        try
        {
            // ProviderMessageId is only there once the channel accepted the reply; SentAt is our own clock.
            await Messages.UpdateManyAsync(
                Builders<MessageDocument>.Filter.Where(x => x.ConnectionId == connection && x.From == customer && x.Outgoing
                    && x.ProviderMessageId != null && x.SentAt <= upTo),
                ReceiptUpdate(state, at), cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "receipt.record_up_to_failed State={State} ConnectionId={ConnectionId}", state, connection);
        }
    }

    // True when a reply on this connection has that channel id, whether or not the receipt changed it.
    private async Task<bool> ApplyAsync(Guid connection, string providerMessageId, DeliveryState state, DateTime at, CancellationToken ct)
    {
        UpdateResult result = await Messages.UpdateOneAsync(
            Builders<MessageDocument>.Filter.Where(x => x.ProviderMessageId == providerMessageId && x.ConnectionId == connection && x.Outgoing),
            ReceiptUpdate(state, at), cancellationToken: ct);
        return result.MatchedCount > 0;
    }

    // Delivery is the earliest time reported, so a "delivered" that arrives after "read" lowers the time
    // the read had filled in. A failure never overturns a reply the customer's device has received.
    private static UpdateDefinition<MessageDocument> ReceiptUpdate(DeliveryState state, DateTime at)
    {
        BsonDateTime when = new(at);
        BsonDocument set = state switch
        {
            DeliveryState.Delivered => new BsonDocument("DeliveredAt", Earliest("DeliveredAt", when)),
            DeliveryState.Read => new BsonDocument { { "DeliveredAt", Earliest("DeliveredAt", when) }, { "ReadAt", Earliest("ReadAt", when) } },
            _ => new BsonDocument("SendStatus", new BsonDocument("$cond", new BsonArray
            {
                new BsonDocument("$or", new BsonArray { IsSet("DeliveredAt"), IsSet("ReadAt") }),
                "$SendStatus",
                "failed",
            })),
        };
        return Builders<MessageDocument>.Update.Pipeline(
            new BsonDocumentStagePipelineDefinition<MessageDocument, MessageDocument>([new BsonDocument("$set", set)]));
    }

    private static BsonDocument Earliest(string field, BsonDateTime when) =>
        new("$min", new BsonArray { "$" + field, when });

    private static BsonDocument IsSet(string field) =>
        new("$ne", new BsonArray { new BsonDocument("$ifNull", new BsonArray { "$" + field, BsonNull.Value }), BsonNull.Value });

    private static FilterDefinition<BsonDocument> ParkedKey(Guid connection, string providerMessageId) =>
        Builders<BsonDocument>.Filter.Eq("ConnectionId", connection.ToString()) & Builders<BsonDocument>.Filter.Eq("ProviderMessageId", providerMessageId);

    private async Task ParkAsync(Guid connection, string providerMessageId, DeliveryState state, DateTime at, CancellationToken ct)
    {
        await EnsureParkedIndexAsync(ct);
        BsonDateTime when = new(at);
        UpdateDefinition<BsonDocument> update = state switch
        {
            DeliveryState.Delivered => Builders<BsonDocument>.Update.Min("DeliveredAt", when),
            DeliveryState.Read => Builders<BsonDocument>.Update.Min("DeliveredAt", when).Min("ReadAt", when),
            _ => Builders<BsonDocument>.Update.Set("Failed", true),
        };
        update = update.SetOnInsert("CreatedAt", DateTime.UtcNow);

        // Two receipts for the same new reply can race to create it; the loser just updates.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await Parked.UpdateOneAsync(ParkedKey(connection, providerMessageId), update, new UpdateOptions { IsUpsert = true }, ct);
                return;
            }
            catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey && attempt == 0) { }
        }
    }

    private async Task EnsureParkedIndexAsync(CancellationToken ct)
    {
        if (_parkedIndexed) return;
        await Parked.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("ConnectionId").Ascending("ProviderMessageId"), new CreateIndexOptions { Unique = true }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("CreatedAt"), new CreateIndexOptions { ExpireAfter = ParkedLifetime }),
        ], ct);
        _parkedIndexed = true;
    }
}
