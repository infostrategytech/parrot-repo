using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Parrot.Application.Agents;
using Parrot.Infrastructure.Messaging;

namespace Parrot.Infrastructure.Agents;

/// <summary>
/// The agents' activity in MongoDB, one document per event. A unique index
/// on the owner, the event's type and its key makes a redelivered event a
/// no-op, so the consumer can commit after a crash without counting anything
/// twice; the owner is part of the key so one tenant's event can never stand
/// in for another's.
///
/// The indexes are made on first use, not in the constructor: the consumer
/// that owns this is a hosted service, and MongoDB being down at boot must
/// cost a retry, not the whole API.
/// </summary>
public sealed class MongoAgentActivityStore : IAgentActivityStore
{
    public const string CollectionName = "agent_activity";
    public const string EventIndexName = "OwnerUserId_Type_EventKey_unique";
    public const string OwnerIndexName = "OwnerUserId_OccurredAt";
    public const string TurnIndexName = "OwnerUserId_TurnId";
    public const string TtlIndexName = "OccurredAt_ttl";

    private static readonly TimeSpan ActivityLifetime = TimeSpan.FromDays(90);

    private readonly IMongoCollection<AgentActivityDocument> _activity;
    private readonly SemaphoreSlim _indexing = new(1, 1);
    private volatile bool _indexed;

    public MongoAgentActivityStore(IMongoClient client, IOptions<MongoDbSettings> settings)
    {
        _activity = client.GetDatabase(settings.Value.Database).GetCollection<AgentActivityDocument>(CollectionName);
    }

    public async Task<bool> InsertAsync(AgentActivityRecord record, CancellationToken cancellationToken = default)
    {
        await EnsureIndexesAsync(cancellationToken);
        try
        {
            await _activity.InsertOneAsync(AgentActivityDocument.From(record), cancellationToken: cancellationToken);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<AgentActivityRecord>> ListAsync(
        string ownerUserId, IReadOnlyCollection<string> types, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default)
    {
        await EnsureIndexesAsync(cancellationToken);
        FilterDefinition<AgentActivityDocument> filter =
            Builders<AgentActivityDocument>.Filter.Eq(x => x.OwnerUserId, ownerUserId)
            & Builders<AgentActivityDocument>.Filter.In(x => x.Type, types)
            & Builders<AgentActivityDocument>.Filter.Gte(x => x.OccurredAt, fromUtc)
            & Builders<AgentActivityDocument>.Filter.Lt(x => x.OccurredAt, toUtc);

        List<AgentActivityDocument> documents = await _activity.Find(filter).SortBy(x => x.OccurredAt).ToListAsync(cancellationToken);
        return documents.Select(d => d.ToRecord()).ToList();
    }

    public async Task<IReadOnlyList<AgentActivityRecord>> ListRecentAsync(string ownerUserId, string type, int limit, CancellationToken cancellationToken = default)
    {
        await EnsureIndexesAsync(cancellationToken);
        FilterDefinition<AgentActivityDocument> filter =
            Builders<AgentActivityDocument>.Filter.Eq(x => x.OwnerUserId, ownerUserId)
            & Builders<AgentActivityDocument>.Filter.Eq(x => x.Type, type);

        List<AgentActivityDocument> documents = await _activity.Find(filter)
            .SortByDescending(x => x.OccurredAt)
            .Limit(Math.Max(1, limit))
            .ToListAsync(cancellationToken);
        return documents.Select(d => d.ToRecord()).ToList();
    }

    public async Task<IReadOnlyList<AgentActivityRecord>> ListByTurnIdsAsync(
        string ownerUserId, IReadOnlyCollection<string> turnIds, CancellationToken cancellationToken = default)
    {
        if (turnIds.Count == 0) return [];
        await EnsureIndexesAsync(cancellationToken);
        FilterDefinition<AgentActivityDocument> filter =
            Builders<AgentActivityDocument>.Filter.Eq(x => x.OwnerUserId, ownerUserId)
            & Builders<AgentActivityDocument>.Filter.In(x => x.TurnId, turnIds);

        List<AgentActivityDocument> documents = await _activity.Find(filter).SortBy(x => x.OccurredAt).ToListAsync(cancellationToken);
        return documents.Select(d => d.ToRecord()).ToList();
    }

    public async Task<IReadOnlyList<AgentActivityRecord>> ListPendingAsync(string ownerUserId, CancellationToken cancellationToken = default)
    {
        await EnsureIndexesAsync(cancellationToken);
        FilterDefinition<AgentActivityDocument> filter =
            Builders<AgentActivityDocument>.Filter.Eq(x => x.OwnerUserId, ownerUserId)
            & Builders<AgentActivityDocument>.Filter.In(x => x.Type, [AgentActivityTypes.ApprovalRequested, AgentActivityTypes.EscalationOpened])
            & Builders<AgentActivityDocument>.Filter.Eq(x => x.ResolvedAt, null);

        List<AgentActivityDocument> documents = await _activity.Find(filter).SortBy(x => x.OccurredAt).ToListAsync(cancellationToken);
        return documents.Select(d => d.ToRecord()).ToList();
    }

    public async Task<int> MarkApprovalDecidedAsync(
        string ownerUserId,
        string approvalId,
        string resolution,
        string decidedBy,
        string? instruction,
        DateTime decidedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await EnsureIndexesAsync(cancellationToken);
        FilterDefinition<AgentActivityDocument> filter =
            Builders<AgentActivityDocument>.Filter.Eq(x => x.OwnerUserId, ownerUserId)
            & Builders<AgentActivityDocument>.Filter.Eq(x => x.ApprovalId, approvalId)
            & Builders<AgentActivityDocument>.Filter.Eq(x => x.ResolvedAt, null);
        UpdateDefinition<AgentActivityDocument> update = Builders<AgentActivityDocument>.Update
            .Set(x => x.ResolvedAt, decidedAtUtc)
            .Set(x => x.Resolution, resolution)
            .Set(x => x.ResolvedBy, decidedBy)
            .Set(x => x.Instruction, instruction);

        UpdateResult result = await _activity.UpdateManyAsync(filter, update, cancellationToken: cancellationToken);
        return (int)result.ModifiedCount;
    }

    private async Task EnsureIndexesAsync(CancellationToken cancellationToken)
    {
        if (_indexed) return;
        await _indexing.WaitAsync(cancellationToken);
        try
        {
            if (_indexed) return;
            await _activity.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<AgentActivityDocument>(
                    Builders<AgentActivityDocument>.IndexKeys.Ascending(x => x.OwnerUserId).Ascending(x => x.Type).Ascending(x => x.EventKey),
                    new CreateIndexOptions { Name = EventIndexName, Unique = true }),
                new CreateIndexModel<AgentActivityDocument>(
                    Builders<AgentActivityDocument>.IndexKeys.Ascending(x => x.OwnerUserId).Descending(x => x.OccurredAt),
                    new CreateIndexOptions { Name = OwnerIndexName }),
                new CreateIndexModel<AgentActivityDocument>(
                    Builders<AgentActivityDocument>.IndexKeys.Ascending(x => x.OwnerUserId).Ascending(x => x.TurnId),
                    new CreateIndexOptions { Name = TurnIndexName }),
                new CreateIndexModel<AgentActivityDocument>(
                    Builders<AgentActivityDocument>.IndexKeys.Ascending(x => x.OccurredAt),
                    new CreateIndexOptions { Name = TtlIndexName, ExpireAfter = ActivityLifetime }),
            ], cancellationToken);
            _indexed = true;
        }
        finally
        {
            _indexing.Release();
        }
    }

    [BsonIgnoreExtraElements]
    public sealed class AgentActivityDocument
    {
        [BsonId]
        public ObjectId Id { get; set; }

        public string OwnerUserId { get; set; } = string.Empty;

        public string Type { get; set; } = string.Empty;

        public string EventKey { get; set; } = string.Empty;

        [BsonIgnoreIfNull]
        public string? ConversationKey { get; set; }

        [BsonIgnoreIfNull]
        public string? TurnId { get; set; }

        [BsonIgnoreIfNull]
        public string? AgentId { get; set; }

        [BsonIgnoreIfNull]
        public string? Decision { get; set; }

        [BsonIgnoreIfNull]
        public double? Confidence { get; set; }

        [BsonIgnoreIfNull]
        public string? Status { get; set; }

        [BsonIgnoreIfNull]
        public string? CustomerRef { get; set; }

        [BsonIgnoreIfNull]
        public string? CustomerName { get; set; }

        [BsonIgnoreIfNull]
        public string? Channel { get; set; }

        [BsonIgnoreIfNull]
        public string? Reply { get; set; }

        [BsonIgnoreIfNull]
        public string? EscalationId { get; set; }

        [BsonIgnoreIfNull]
        public string? ApprovalId { get; set; }

        [BsonIgnoreIfNull]
        public string? Priority { get; set; }

        [BsonIgnoreIfNull]
        public string? Problem { get; set; }

        [BsonIgnoreIfNull]
        public string? Tool { get; set; }

        [BsonIgnoreIfNull]
        public string? Reason { get; set; }

        [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
        public DateTime OccurredAt { get; set; }

        [BsonIgnoreIfNull]
        public string? CorrelationId { get; set; }

        public string Payload { get; set; } = "{}";

        [BsonIgnoreIfNull]
        [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
        public DateTime? ResolvedAt { get; set; }

        [BsonIgnoreIfNull]
        public string? Resolution { get; set; }

        [BsonIgnoreIfNull]
        public string? ResolvedBy { get; set; }

        [BsonIgnoreIfNull]
        public string? Instruction { get; set; }

        public static AgentActivityDocument From(AgentActivityRecord record) => new()
        {
            OwnerUserId = record.OwnerUserId,
            Type = record.Type,
            EventKey = record.EventKey,
            ConversationKey = record.ConversationKey,
            TurnId = record.TurnId,
            AgentId = record.AgentId,
            Decision = record.Decision,
            Confidence = record.Confidence,
            Status = record.Status,
            CustomerRef = record.CustomerRef,
            CustomerName = record.CustomerName,
            Channel = record.Channel,
            Reply = record.Reply,
            EscalationId = record.EscalationId,
            ApprovalId = record.ApprovalId,
            Priority = record.Priority,
            Problem = record.Problem,
            Tool = record.Tool,
            Reason = record.Reason,
            OccurredAt = DateTime.SpecifyKind(record.OccurredAt, DateTimeKind.Utc),
            CorrelationId = record.CorrelationId,
            Payload = record.Payload,
            ResolvedAt = record.ResolvedAt,
            Resolution = record.Resolution,
            ResolvedBy = record.ResolvedBy,
            Instruction = record.Instruction,
        };

        public AgentActivityRecord ToRecord() => new()
        {
            OwnerUserId = OwnerUserId,
            Type = Type,
            EventKey = EventKey,
            ConversationKey = ConversationKey,
            TurnId = TurnId,
            AgentId = AgentId,
            Decision = Decision,
            Confidence = Confidence,
            Status = Status,
            CustomerRef = CustomerRef,
            CustomerName = CustomerName,
            Channel = Channel,
            Reply = Reply,
            EscalationId = EscalationId,
            ApprovalId = ApprovalId,
            Priority = Priority,
            Problem = Problem,
            Tool = Tool,
            Reason = Reason,
            OccurredAt = OccurredAt,
            CorrelationId = CorrelationId,
            Payload = Payload,
            ResolvedAt = ResolvedAt,
            Resolution = Resolution,
            ResolvedBy = ResolvedBy,
            Instruction = Instruction,
        };
    }
}
