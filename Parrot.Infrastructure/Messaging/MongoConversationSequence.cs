using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Parrot.Application.Messaging;

namespace Parrot.Infrastructure.Messaging;

/// <summary>
/// One counter document per conversation in <c>conversation_counters</c>,
/// <c>{ _id: conversationKey, value: n }</c>. <c>$inc</c> with upsert is atomic
/// on a single document, so two callers never get the same number, and the
/// first call for a conversation creates its counter and returns 1.
/// </summary>
public sealed class MongoConversationSequence(IMongoClient client, IOptions<MongoDbSettings> settings) : IConversationSequence
{
    public const string CollectionName = "conversation_counters";

    private readonly IMongoCollection<Counter> _counters =
        client.GetDatabase(settings.Value.Database).GetCollection<Counter>(CollectionName);

    public async Task<long> NextAsync(string conversationKey, CancellationToken cancellationToken = default)
    {
        Counter counter = await _counters.FindOneAndUpdateAsync(
            Builders<Counter>.Filter.Eq(c => c.Id, conversationKey),
            Builders<Counter>.Update.Inc(c => c.Value, 1L),
            new FindOneAndUpdateOptions<Counter> { IsUpsert = true, ReturnDocument = ReturnDocument.After },
            cancellationToken);
        return counter.Value;
    }

    public sealed class Counter
    {
        [BsonId]
        public required string Id { get; init; }

        [BsonElement("value")]
        public long Value { get; init; }
    }
}
