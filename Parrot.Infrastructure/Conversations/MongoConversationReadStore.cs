using System.Buffers.Binary;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Parrot.Application.Conversations;
using Parrot.Domain.Entities.Messages;
using Parrot.Domain.Exceptions;
using Parrot.Infrastructure.Messaging;

namespace Parrot.Infrastructure.Conversations;

/// <summary>
/// Where unread counting starts. Messages stored before it count as read, so turning
/// the feature on does not show a user their whole history as unread.
/// </summary>
public sealed class InboxSettings
{
    public const string SectionName = "Inbox";

    /// <summary>
    /// Optional. When not set, the time the API first started with this feature is
    /// saved in MongoDB and used from then on. It must never move later: that would
    /// mark messages that really are unread as read.
    /// </summary>
    public DateTime? UnreadBaseline { get; set; }
}

public sealed class MongoConversationReadStore : IConversationReadStore
{
    // Conditions per query: one OR branch per conversation.
    private const int CountBatch = 200;

    private readonly IMongoCollection<MessageDocument> _messages;
    private readonly IMongoCollection<ConversationReadMark> _marks;
    private readonly ObjectId _baseline;

    public MongoConversationReadStore(IMongoClient client, IOptions<MongoDbSettings> mongo, IOptions<InboxSettings> inbox)
    {
        IMongoDatabase database = client.GetDatabase(mongo.Value.Database);
        _messages = database.GetCollection<MessageDocument>(mongo.Value.Collection);
        _marks = database.GetCollection<ConversationReadMark>("conversation_reads");
        _marks.Indexes.CreateOne(new CreateIndexModel<ConversationReadMark>(
            Builders<ConversationReadMark>.IndexKeys
                .Ascending(x => x.ReaderUserId).Ascending(x => x.ConnectionId).Ascending(x => x.Sender),
            new CreateIndexOptions { Unique = true }));
        _baseline = FloorOf(inbox.Value.UnreadBaseline?.ToUniversalTime() ?? FirstStart(database));
    }

    public async Task MarkReadAsync(string owner, string reader, Guid connection, string sender, string upToMessageId, CancellationToken ct)
    {
        if (!ObjectId.TryParse(upToMessageId, out ObjectId upTo)) throw new ValidationException("Invalid message ID.");

        // The message must be in this conversation. Otherwise an id from the future would
        // mark every later message read before it arrived.
        bool exists = await _messages.Find(x => x.Id == upToMessageId && x.OwnerUserId == owner
            && x.ConnectionId == connection && x.From == sender).AnyAsync(ct);
        if (!exists) throw new NotFoundException("Message not found.");

        // Only a later position matches. When the stored one is already later nothing matches,
        // the upsert then collides with the unique key, and that means there is nothing to do.
        FilterDefinition<ConversationReadMark> filter = Builders<ConversationReadMark>.Filter.Where(
            x => x.ReaderUserId == reader && x.ConnectionId == connection && x.Sender == sender);
        try
        {
            await _marks.UpdateOneAsync(
                filter & Builders<ConversationReadMark>.Filter.Lt(x => x.LastReadMessageId, upToMessageId),
                Builders<ConversationReadMark>.Update.Set(x => x.LastReadMessageId, upToMessageId).Set(x => x.UpdatedAt, DateTime.UtcNow),
                new UpdateOptions { IsUpsert = true }, ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey) { }
    }

    public async Task<IReadOnlyDictionary<(Guid Connection, string Sender), int>> CountUnreadAsync(
        string owner, string reader, IReadOnlyCollection<(Guid Connection, string Sender)> conversations, CancellationToken ct)
    {
        Dictionary<(Guid, string), ObjectId> marks = await LoadMarksAsync(reader, ct);
        return await CountAfterAsync(owner,
            conversations.Select(c => (c.Connection, c.Sender, Watermark(marks, c.Connection, c.Sender))).ToList(), ct);
    }

    public async Task<int> TotalUnreadAsync(string owner, string reader, CancellationToken ct)
    {
        Dictionary<(Guid, string), ObjectId> marks = await LoadMarksAsync(reader, ct);

        // The newest incoming message of every conversation since the baseline. Only a
        // conversation whose newest message is past the user's position has anything unread,
        // and only those are counted.
        List<BsonDocument> latest = await _messages.Aggregate()
            .Match(IncomingSince(owner))
            .AppendStage<BsonDocument>(new BsonDocument("$group", new BsonDocument
            {
                { "_id", new BsonDocument { { "connection", "$ConnectionId" }, { "sender", "$From" } } },
                { "newest", new BsonDocument("$max", "$_id") },
            }))
            .ToListAsync(ct);

        List<(Guid Connection, string Sender, ObjectId After)> behind = [];
        foreach (BsonDocument row in latest)
        {
            Guid connection = Guid.Parse(row["_id"]["connection"].AsString);
            string sender = row["_id"]["sender"].AsString;
            ObjectId after = Watermark(marks, connection, sender);
            if (row["newest"].AsObjectId.CompareTo(after) > 0) behind.Add((connection, sender, after));
        }
        return (await CountAfterAsync(owner, behind, ct)).Values.Sum();
    }

    // Incoming (not the agent's own) messages of this owner's conversations, after the baseline.
    private FilterDefinition<MessageDocument> IncomingSince(string owner) =>
        Builders<MessageDocument>.Filter.Where(x => x.OwnerUserId == owner && x.ConnectionId != null)
        & Builders<MessageDocument>.Filter.Ne(x => x.Outgoing, true)
        & Builders<MessageDocument>.Filter.Gt(x => x.Id, _baseline.ToString());

    private async Task<Dictionary<(Guid, string), int>> CountAfterAsync(
        string owner, IReadOnlyList<(Guid Connection, string Sender, ObjectId After)> conversations, CancellationToken ct)
    {
        Dictionary<(Guid, string), int> counts = [];
        foreach ((Guid Connection, string Sender, ObjectId After)[] batch in conversations.Chunk(CountBatch))
        {
            FilterDefinition<MessageDocument> inConversation = Builders<MessageDocument>.Filter.Or(batch.Select(c =>
                Builders<MessageDocument>.Filter.Where(x => x.ConnectionId == c.Connection && x.From == c.Sender)
                & Builders<MessageDocument>.Filter.Gt(x => x.Id, c.After.ToString())));
            List<BsonDocument> rows = await _messages.Aggregate()
                .Match(Builders<MessageDocument>.Filter.Where(x => x.OwnerUserId == owner)
                    & Builders<MessageDocument>.Filter.Ne(x => x.Outgoing, true) & inConversation)
                .AppendStage<BsonDocument>(new BsonDocument("$group", new BsonDocument
                {
                    { "_id", new BsonDocument { { "connection", "$ConnectionId" }, { "sender", "$From" } } },
                    { "count", new BsonDocument("$sum", 1) },
                }))
                .ToListAsync(ct);
            foreach (BsonDocument row in rows)
                counts[(Guid.Parse(row["_id"]["connection"].AsString), row["_id"]["sender"].AsString)] = row["count"].ToInt32();
        }
        return counts;
    }

    private async Task<Dictionary<(Guid, string), ObjectId>> LoadMarksAsync(string reader, CancellationToken ct)
    {
        List<ConversationReadMark> marks = await _marks.Find(x => x.ReaderUserId == reader).ToListAsync(ct);
        return marks.ToDictionary(m => (m.ConnectionId, m.Sender), m => ObjectId.Parse(m.LastReadMessageId));
    }

    // Past the user's position and the baseline, whichever is later.
    private ObjectId Watermark(Dictionary<(Guid, string), ObjectId> marks, Guid connection, string sender) =>
        marks.TryGetValue((connection, sender), out ObjectId mark) && mark.CompareTo(_baseline) > 0 ? mark : _baseline;

    // The smallest ObjectId of that second, so every message stored from then on is after it.
    private static ObjectId FloorOf(DateTime utc)
    {
        byte[] bytes = new byte[12];
        BinaryPrimitives.WriteInt32BigEndian(bytes, (int)new DateTimeOffset(utc).ToUnixTimeSeconds());
        return new ObjectId(bytes);
    }

    // Saved once, so a restart keeps it where it was.
    private static DateTime FirstStart(IMongoDatabase database) =>
        database.GetCollection<BsonDocument>("inbox_state").FindOneAndUpdate(
            new BsonDocument("_id", "unread-baseline"),
            Builders<BsonDocument>.Update.SetOnInsert("At", DateTime.UtcNow),
            new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After })["At"].ToUniversalTime();
}

[BsonIgnoreExtraElements]
internal sealed class ConversationReadMark
{
    [BsonId]
    public ObjectId Id { get; set; }
    public string ReaderUserId { get; set; } = "";
    [BsonRepresentation(BsonType.String)]
    public Guid ConnectionId { get; set; }
    public string Sender { get; set; } = "";
    [BsonRepresentation(BsonType.ObjectId)]
    public string LastReadMessageId { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}
