using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Parrot.Application.Conversations;
using Parrot.Domain.Entities.Messages;
using Parrot.Infrastructure.Messaging;

namespace Parrot.Infrastructure.Conversations;

// Used on the webhook path, so it does nothing in its constructor: the webhook
// must not depend on MongoDB being up. The caller bounds the query's time.
// The ProviderMessageId index is created by MongoConversationStore.
public sealed class MongoReplySendTimes(IMongoClient client, IOptions<MongoDbSettings> options) : IReplySendTimes
{
    private readonly IMongoCollection<MessageDocument> _messages =
        client.GetDatabase(options.Value.Database).GetCollection<MessageDocument>(options.Value.Collection);

    public async Task<DateTime?> SentAtAsync(string providerMessageId, CancellationToken ct) =>
        await _messages.Find(x => x.ProviderMessageId == providerMessageId && x.Outgoing, new FindOptions { MaxTime = TimeSpan.FromSeconds(1) })
            .Project(x => x.SentAt).FirstOrDefaultAsync(ct);
}
