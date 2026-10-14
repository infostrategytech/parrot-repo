using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Parrot.Application.Conversations;
using Parrot.Infrastructure.Conversations;
using Parrot.Infrastructure.Messaging;
using static Test.Parrot.Api.Tests.Services.MongoConversationTests;

namespace Test.Parrot.Api.Tests.Services;

// Internal notes against a real, temporary MongoDB.
public class ConversationNoteTests
{
    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _database = "parrot_test_" + Guid.NewGuid().ToString("N");
        private readonly MongoClient _client = new(Environment.GetEnvironmentVariable("PARROT_TEST_MONGO"));

        public MongoConversationNoteStore Notes() =>
            new(_client, Options.Create(new MongoDbSettings { Database = _database, Collection = "messages" }));

        public async ValueTask DisposeAsync() => await _client.DropDatabaseAsync(_database);
    }

    [MongoFact]
    public async Task NotesBelongToOneConversationAndComeBackOldestFirst()
    {
        await using Harness h = new();
        MongoConversationNoteStore notes = h.Notes();
        Guid connection = Guid.NewGuid();

        await notes.AddAsync("owner", connection, "ada", "first", "u1", "ade@example.com", default);
        await notes.AddAsync("owner", connection, "ada", "second", "u1", "ade@example.com", default);
        await notes.AddAsync("owner", connection, "bola", "another customer", "u1", "ade@example.com", default);

        IReadOnlyList<ConversationNote> ada = await notes.ListAsync("owner", connection, "ada", default);
        Assert.Equal(["first", "second"], ada.Select(n => n.Text));
        Assert.Equal("ade@example.com", ada[0].AuthorName);
        Assert.Equal("another customer", Assert.Single(await notes.ListAsync("owner", connection, "bola", default)).Text);
        Assert.Empty(await notes.ListAsync("owner", Guid.NewGuid(), "ada", default));
    }

    [MongoFact]
    public async Task AnotherOwnerSeesAndCannotDeleteTheNotes()
    {
        await using Harness h = new();
        MongoConversationNoteStore notes = h.Notes();
        Guid connection = Guid.NewGuid();
        ConversationNote note = await notes.AddAsync("owner", connection, "ada", "private", "u1", "a", default);

        Assert.Empty(await notes.ListAsync("someone-else", connection, "ada", default));
        Assert.False(await notes.DeleteAsync("someone-else", connection, "ada", note.Id, default));
        Assert.Single(await notes.ListAsync("owner", connection, "ada", default));
    }

    [MongoFact]
    public async Task DeletingRemovesOnlyThatNoteAndATwiceDeleteOrBadIdIsFalse()
    {
        await using Harness h = new();
        MongoConversationNoteStore notes = h.Notes();
        Guid connection = Guid.NewGuid();
        ConversationNote keep = await notes.AddAsync("owner", connection, "ada", "keep", "u1", "a", default);
        ConversationNote drop = await notes.AddAsync("owner", connection, "ada", "drop", "u1", "a", default);

        Assert.True(await notes.DeleteAsync("owner", connection, "ada", drop.Id, default));
        Assert.False(await notes.DeleteAsync("owner", connection, "ada", drop.Id, default));
        Assert.False(await notes.DeleteAsync("owner", connection, "ada", "not-an-id", default));
        Assert.False(await notes.DeleteAsync("owner", connection, "bola", keep.Id, default));
        Assert.Equal(keep.Id, Assert.Single(await notes.ListAsync("owner", connection, "ada", default)).Id);
    }
}
