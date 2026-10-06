using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Parrot.Application.Messaging;
using Parrot.Domain.Entities.Messages;

namespace Parrot.Infrastructure.Messaging;

/// <summary>
/// The worker's writes to the messages collection. The unique MessageId index
/// (created by <see cref="MongoMessageRepository"/>) is what makes a second
/// insert of one message impossible; this turns that into "return the stored one".
/// </summary>
public sealed class MongoStoredMessageStore(IMongoClient client, IOptions<MongoDbSettings> settings) : IStoredMessageStore
{
    private readonly IMongoCollection<MessageDocument> _messages =
        client.GetDatabase(settings.Value.Database).GetCollection<MessageDocument>(settings.Value.Collection);

    public async Task<MessageDocument?> FindAsync(string messageId, CancellationToken cancellationToken = default) =>
        await _messages.Find(d => d.MessageId == messageId).FirstOrDefaultAsync(cancellationToken);

    public async Task<MessageDocument> InsertOrGetAsync(MessageDocument document, CancellationToken cancellationToken = default)
    {
        try
        {
            await _messages.InsertOneAsync(document, cancellationToken: cancellationToken);
            return document;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey
            && ex.WriteError.Message.Contains("MessageId", StringComparison.Ordinal))
        {
            // Stored by an earlier delivery between our lookup and this insert.
            return await FindAsync(document.MessageId, cancellationToken)
                ?? throw new InvalidOperationException("A duplicate MessageId was reported but the stored message could not be read.", ex);
        }
    }
}
