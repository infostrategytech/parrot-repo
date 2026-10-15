using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Parrot.Application.DTOs.Integrations;
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

    public async Task<bool> CompleteMediaAsync(string messageId, int index, IncomingMedia outcome, CancellationToken cancellationToken = default)
    {
        FilterDefinitionBuilder<MessageDocument> filter = Builders<MessageDocument>.Filter;
        UpdateDefinitionBuilder<MessageDocument> update = Builders<MessageDocument>.Update;

        // The status in the filter makes this a compare-and-set: of two workers finishing one file, one wins.
        UpdateResult result = await _messages.UpdateOneAsync(
            filter.Eq(d => d.MessageId, messageId) & filter.Eq(d => d.Attachments![index].Status, MediaStatus.Pending),
            update.Combine(
                update.Set(d => d.Attachments![index].Status, outcome.Status),
                update.Set(d => d.Attachments![index].FailureReason, outcome.FailureReason),
                update.Set(d => d.Attachments![index].StorageKey, outcome.StorageKey),
                update.Set(d => d.Attachments![index].Kind, outcome.Kind),
                update.Set(d => d.Attachments![index].ContentType, outcome.ContentType),
                update.Set(d => d.Attachments![index].SizeBytes, outcome.SizeBytes),
                update.Set(d => d.Attachments![index].SourceUrl, null)),
            cancellationToken: cancellationToken);
        return result.ModifiedCount > 0;
    }

    public async Task<bool> ClaimSendAsync(string messageId, DateTime claimedAt, CancellationToken cancellationToken = default)
    {
        UpdateResult result = await _messages.UpdateOneAsync(
            d => d.MessageId == messageId && d.Outgoing && d.SendStatus == "pending",
            Builders<MessageDocument>.Update.Set(d => d.SendStatus, "sending").Set(d => d.SendClaimedAt, claimedAt),
            cancellationToken: cancellationToken);
        return result.ModifiedCount > 0;
    }

    public async Task<bool> CompleteSendAsync(string messageId, string status, string? providerId, CancellationToken cancellationToken = default)
    {
        UpdateResult result = await _messages.UpdateOneAsync(
            d => d.MessageId == messageId && d.SendStatus == "sending",
            Builders<MessageDocument>.Update.Set(d => d.SendStatus, status).Set(d => d.ProviderMessageId, providerId),
            cancellationToken: cancellationToken);
        return result.ModifiedCount > 0;
    }

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
