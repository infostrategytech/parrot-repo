using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Parrot.Application.Conversations;
using Parrot.Infrastructure.Messaging;

namespace Parrot.Infrastructure.Conversations;

public sealed class MongoConversationNoteStore : IConversationNoteStore
{
    private readonly IMongoCollection<ConversationNoteDocument> _notes;

    public MongoConversationNoteStore(IMongoClient client, IOptions<MongoDbSettings> mongo)
    {
        _notes = client.GetDatabase(mongo.Value.Database).GetCollection<ConversationNoteDocument>("conversation_notes");
        _notes.Indexes.CreateOne(new CreateIndexModel<ConversationNoteDocument>(
            Builders<ConversationNoteDocument>.IndexKeys
                .Ascending(x => x.OwnerUserId).Ascending(x => x.ConnectionId).Ascending(x => x.Sender).Ascending(x => x.Id)));
    }

    public async Task<IReadOnlyList<ConversationNote>> ListAsync(string owner, Guid connection, string sender, CancellationToken ct)
    {
        List<ConversationNoteDocument> rows = await _notes
            .Find(x => x.OwnerUserId == owner && x.ConnectionId == connection && x.Sender == sender)
            .SortBy(x => x.Id).ToListAsync(ct);
        return rows.Select(ToNote).ToList();
    }

    public async Task<ConversationNote> AddAsync(string owner, Guid connection, string sender, string text, string authorId, string authorName, CancellationToken ct)
    {
        ConversationNoteDocument note = new()
        {
            Id = ObjectId.GenerateNewId(), OwnerUserId = owner, ConnectionId = connection, Sender = sender,
            Text = text, AuthorId = authorId, AuthorName = authorName, CreatedAt = DateTime.UtcNow,
        };
        await _notes.InsertOneAsync(note, cancellationToken: ct);
        return ToNote(note);
    }

    public async Task<bool> DeleteAsync(string owner, Guid connection, string sender, string noteId, CancellationToken ct)
    {
        if (!ObjectId.TryParse(noteId, out ObjectId id)) return false;
        DeleteResult result = await _notes.DeleteOneAsync(
            x => x.Id == id && x.OwnerUserId == owner && x.ConnectionId == connection && x.Sender == sender, ct);
        return result.DeletedCount > 0;
    }

    private static ConversationNote ToNote(ConversationNoteDocument d) =>
        new(d.Id.ToString(), d.Text, d.AuthorId, d.AuthorName, DateTime.SpecifyKind(d.CreatedAt, DateTimeKind.Utc));
}

[BsonIgnoreExtraElements]
internal sealed class ConversationNoteDocument
{
    [BsonId]
    public ObjectId Id { get; set; }
    public string OwnerUserId { get; set; } = "";
    [BsonRepresentation(BsonType.String)]
    public Guid ConnectionId { get; set; }
    public string Sender { get; set; } = "";
    public string Text { get; set; } = "";
    public string AuthorId { get; set; } = "";
    public string AuthorName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
