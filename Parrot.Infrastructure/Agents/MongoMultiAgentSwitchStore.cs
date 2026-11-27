using Microsoft.Extensions.Options;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Parrot.Application.Agents;
using Parrot.Infrastructure.Messaging;

namespace Parrot.Infrastructure.Agents;

/// <summary>
/// One document per workspace that has used the switch, keyed by its id; none means off. Temporary: the collection
/// is dropped when the switch is removed.
/// </summary>
public sealed class MongoMultiAgentSwitchStore(IMongoClient client, IOptions<MongoDbSettings> mongo) : IMultiAgentSwitchStore
{
    public const string CollectionName = "multi_agent_switches";

    private readonly IMongoCollection<MultiAgentSwitchDocument> _switches =
        client.GetDatabase(mongo.Value.Database).GetCollection<MultiAgentSwitchDocument>(CollectionName);

    public async Task<bool> IsOnAsync(string workspaceId, CancellationToken cancellationToken) =>
        await _switches.Find(s => s.WorkspaceId == workspaceId).Project(s => s.IsOn).FirstOrDefaultAsync(cancellationToken);

    public Task SetAsync(string workspaceId, bool isOn, string changedBy, CancellationToken cancellationToken) =>
        _switches.UpdateOneAsync(s => s.WorkspaceId == workspaceId,
            Builders<MultiAgentSwitchDocument>.Update
                .Set(s => s.IsOn, isOn)
                .Set(s => s.ChangedBy, changedBy)
                .Set(s => s.ChangedAt, DateTime.UtcNow),
            new UpdateOptions { IsUpsert = true }, cancellationToken);

    [BsonIgnoreExtraElements]
    public sealed class MultiAgentSwitchDocument
    {
        [BsonId]
        public string WorkspaceId { get; set; } = "";

        public bool IsOn { get; set; }

        /// <summary>The user who last changed it.</summary>
        public string ChangedBy { get; set; } = "";

        public DateTime ChangedAt { get; set; }
    }
}
