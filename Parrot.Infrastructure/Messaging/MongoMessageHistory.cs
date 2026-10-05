using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Parrot.Application.Messaging;
using Parrot.Domain.Entities.Messages;

namespace Parrot.Infrastructure.Messaging;

/// <summary>
/// One CRM user's messages in one conversation, after a sequence, oldest
/// first, customer messages and the business's replies together. Only numbered
/// messages are returned: those stored before the worker existed, and replies
/// to them, have no sequence.
/// </summary>
public sealed class MongoMessageHistory : IMessageHistory
{
    /// <summary>The most messages one page returns, the same cap as the inbox.</summary>
    public const int MaxPageSize = 100;

    public const string IndexName = "OwnerUserId_ConversationKey_Sequence";

    private readonly IMongoCollection<MessageDocument> _messages;

    public MongoMessageHistory(IMongoClient client, IOptions<MongoDbSettings> settings)
    {
        _messages = client.GetDatabase(settings.Value.Database).GetCollection<MessageDocument>(settings.Value.Collection);

        // Owner and conversation first, then the sequence, so a page is one
        // range scan already in order. Not partial: MongoDB won't use the
        // partial (ConversationKey, Sequence) index for a "greater than" query.
        _messages.Indexes.CreateOne(new CreateIndexModel<MessageDocument>(
            Builders<MessageDocument>.IndexKeys
                .Ascending(x => x.OwnerUserId)
                .Ascending(x => x.ConversationKey)
                .Ascending(x => x.Sequence),
            new CreateIndexOptions { Name = IndexName }));
    }

    public async Task<MessageHistoryPage> GetAfterAsync(
        string ownerUserId,
        string conversationKey,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationKey);
        int pageSize = Math.Clamp(limit, 1, MaxPageSize);

        FilterDefinition<MessageDocument> filter =
            Builders<MessageDocument>.Filter.Eq(x => x.OwnerUserId, ownerUserId)
            & Builders<MessageDocument>.Filter.Eq(x => x.ConversationKey, conversationKey)
            & Builders<MessageDocument>.Filter.Gt(x => x.Sequence, afterSequence);

        // One more than a page, to know whether another page follows.
        List<MessageDocument> documents = await _messages.Find(filter)
            .SortBy(x => x.Sequence)
            .Limit(pageSize + 1)
            .ToListAsync(cancellationToken);

        List<StoredMessage> messages = documents.Take(pageSize).Select(d => d.ToStoredMessage()).ToList();

        return new MessageHistoryPage
        {
            Messages = messages,
            NextCursor = documents.Count > pageSize ? messages[^1].Sequence : null,
        };
    }
}
