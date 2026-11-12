using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Parrot.Application.Conversations;
using Parrot.Application.Messaging;
using Parrot.Domain.Entities.Messages;
using Parrot.Domain.Enums;
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

        // MongoReplySendTimes finds a reply by Meta's wamid. Sparse: only sent replies have one.
        _messages.Indexes.CreateOne(new CreateIndexModel<MessageDocument>(
            Builders<MessageDocument>.IndexKeys.Ascending(x => x.ProviderMessageId), new CreateIndexOptions { Sparse = true }));
    }

    public Task<IReadOnlyList<ConversationSummary>> ListAsync(string owner, int skip, int take, CancellationToken ct) =>
        SummariesAsync(Builders<MessageDocument>.Filter.Where(x => x.OwnerUserId == owner && x.ConnectionId != null), skip, take, ct);

    // The filter is applied before grouping and paging, so a page is full of reachable conversations.
    public Task<IReadOnlyList<ConversationSummary>> ListAsync(string owner, IReadOnlySet<Guid> connections, int skip, int take, CancellationToken ct) =>
        SummariesAsync(Builders<MessageDocument>.Filter.Where(x => x.OwnerUserId == owner && x.ConnectionId != null)
            & Builders<MessageDocument>.Filter.In(x => x.ConnectionId, connections.Select(c => (Guid?)c)), skip, take, ct);

    public async Task<IReadOnlyList<ConversationSummary>> FindAsync(
        string owner, IReadOnlyCollection<(Guid Connection, string Sender)> conversations, CancellationToken ct)
    {
        if (conversations.Count == 0) return [];
        FilterDefinition<MessageDocument> filter = Builders<MessageDocument>.Filter.Where(x => x.OwnerUserId == owner)
            & Builders<MessageDocument>.Filter.Or(conversations.Select(c =>
                Builders<MessageDocument>.Filter.Where(x => x.ConnectionId == c.Connection && x.From == c.Sender)));
        return await SummariesAsync(filter, 0, conversations.Count, ct);
    }

    private async Task<IReadOnlyList<ConversationSummary>> SummariesAsync(FilterDefinition<MessageDocument> match, int skip, int take, CancellationToken ct)
    {
        // System events are stored in the same collection but must not influence the preview text,
        // the contact name, or LastCustomerMessageAt — only real channel messages do.
        FilterDefinition<MessageDocument> withoutSystemEvents = match & Builders<MessageDocument>.Filter.Ne(x => x.Type, "system");
        List<BsonDocument> rows = await _messages.Aggregate()
            .Match(withoutSystemEvents)
            .SortByDescending(x => x.Id)
            .AppendStage<BsonDocument>(new BsonDocument("$group", new BsonDocument
            {
                { "_id", new BsonDocument { { "connection", "$ConnectionId" }, { "sender", "$From" } } },
                { "latest", new BsonDocument("$first", "$$ROOT") },
                { "name", new BsonDocument("$max", "$ContactName") },
                { "username", new BsonDocument("$max", "$ContactUsername") },

                // The customer's own newest message (not the agent's reply), by the time the send check uses.
                // $max skips the nulls the replies give.
                { "lastIncoming", new BsonDocument("$max", new BsonDocument("$cond", new BsonArray
                {
                    new BsonDocument("$ne", new BsonArray { "$Outgoing", true }),
                    new BsonDocument("$ifNull", new BsonArray { "$SentAt", "$ReceivedAt" }),
                    BsonNull.Value,
                })) },
            }))
            .AppendStage<BsonDocument>(new BsonDocument("$sort", new BsonDocument("latest._id", -1)))
            .Skip(skip).Limit(take).ToListAsync(ct);
        return rows.Select(row =>
        {
            MessageDocument d = BsonSerializer.Deserialize<MessageDocument>(row["latest"].AsBsonDocument);
            string name = row["name"].IsString ? row["name"].AsString : d.From;
            return new ConversationSummary(d.ConnectionId!.Value, d.From, name,
                d.TextBody ?? (d.Attachments is { Count: > 0 } ? MediaKinds.PreviewLabel(d.Attachments[0].Kind) : $"[{d.Type} message]"), d.ReceivedAt)
            {
                Channel = d.Platform.ToString().ToLowerInvariant(),
                AccountId = d.ExternalAccountId,
                Username = row["username"].IsString ? row["username"].AsString : null,
                LastCustomerMessageAt = row["lastIncoming"].IsBsonNull ? null : row["lastIncoming"].ToUniversalTime(),
                ReplyWindowHours = (int)ReplyWindow.For(d.Platform).TotalHours,
            };
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

    public async Task<MediaPage> ListMediaAsync(string owner, Guid connection, string sender, string? before, CancellationToken ct)
    {
        const int pageSize = 60;
        FilterDefinition<MessageDocument> filter = Builders<MessageDocument>.Filter.Where(x => x.OwnerUserId == owner && x.ConnectionId == connection && x.From == sender)
            & Builders<MessageDocument>.Filter.Exists(x => x.Attachments);
        if (before is not null)
        {
            if (!ObjectId.TryParse(before, out ObjectId id)) throw new ValidationException("Invalid media cursor.");
            filter &= new BsonDocument("_id", new BsonDocument("$lt", id));
        }

        // A page is whole messages, so the cursor never splits one message's files across two pages.
        List<MessageDocument> docs = await _messages.Find(filter).SortByDescending(x => x.Id).Limit(pageSize + 1).ToListAsync(ct);
        List<MessageDocument> page = docs.Take(pageSize).ToList();
        return new MediaPage(
            page.SelectMany(d => d.Attachments!.Select(a => new MediaItem(d.MessageId, d.ReceivedAt, ConversationService.ToMedia(a)))).ToList(),
            docs.Count > pageSize ? page[^1].Id : null);
    }

    public async Task<MessageDocument?> LatestIncomingAsync(string owner, Guid connection, string sender, CancellationToken ct)
    {
        FilterDefinition<MessageDocument> filter = Builders<MessageDocument>.Filter.Where(
            x => x.OwnerUserId == owner && x.ConnectionId == connection && x.From == sender)
            & Builders<MessageDocument>.Filter.Ne(x => x.Outgoing, true);
        // Ordered by ReceivedAt: Timestamp is a provider string whose units differ per platform.
        return await _messages.Find(filter).SortByDescending(x => x.ReceivedAt).FirstOrDefaultAsync(ct);
    }

    public async Task<MessageDocument?> FindRequestAsync(string owner, Guid request, CancellationToken ct) =>
        await _messages.Find(x => x.OwnerUserId == owner && x.MessageId == ConversationService.RequestKey(owner, request)).FirstOrDefaultAsync(ct);

    public async Task<MessageDocument?> FindMessageAsync(string owner, Guid connection, string sender, string messageId, CancellationToken ct) =>
        await _messages.Find(x => x.MessageId == messageId && x.OwnerUserId == owner && x.ConnectionId == connection && x.From == sender)
            .FirstOrDefaultAsync(ct);

    public async Task<bool> ReserveAsync(MessageDocument message, CancellationToken ct)
    {
        try { await _messages.InsertOneAsync(message, cancellationToken: ct); return true; }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey) { return false; }
    }

    public async Task<bool> FailPendingAsync(string id, CancellationToken ct) =>
        (await _messages.UpdateOneAsync(x => x.MessageId == id && x.SendStatus == "pending",
            Builders<MessageDocument>.Update.Set(x => x.SendStatus, "failed"), cancellationToken: ct)).ModifiedCount > 0;

    public async Task WriteSystemEventAsync(string owner, Guid connection, string sender, string text, CancellationToken ct)
    {
        // Borrow context from the most recent message so the event sits in the same channel.
        MessageDocument? context = await _messages
            .Find(x => x.OwnerUserId == owner && x.ConnectionId == connection && x.From == sender)
            .SortByDescending(x => x.Id)
            .FirstOrDefaultAsync(ct);
        if (context is null) return;

        SystemEventDocument doc = SystemEventDocument.Create(
            owner, connection, sender,
            context.Platform, context.ExternalAccountId, context.ChannelId, context.ChannelDisplayName,
            text);
        try { await _messages.InsertOneAsync(doc, cancellationToken: ct); }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey) { }
    }

    public async Task CompleteAsync(string id, string status, string? providerId, CancellationToken ct) =>
        await _messages.UpdateOneAsync(x => x.MessageId == id,
            Builders<MessageDocument>.Update.Set(x => x.SendStatus, status).Set(x => x.ProviderMessageId, providerId), cancellationToken: ct);
}
