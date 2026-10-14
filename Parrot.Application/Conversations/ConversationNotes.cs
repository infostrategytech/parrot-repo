namespace Parrot.Application.Conversations;

/// <summary>A private note on one conversation, written by a team member. The customer never receives it.</summary>
public sealed record ConversationNote(string Id, string Text, string AuthorId, string AuthorName, DateTime CreatedAt);

public sealed record AddNoteRequest(string Text);

/// <summary>
/// Internal notes. They live in their own collection, apart from the messages, so a note can never be sent to
/// the customer, be counted as an unread message, or move the conversation up the list.
/// </summary>
public interface IConversationNoteStore
{
    /// <summary>The longest note, as long as the composer allows a reply.</summary>
    public const int MaxLength = 4096;

    /// <summary>The conversation's notes, oldest first.</summary>
    Task<IReadOnlyList<ConversationNote>> ListAsync(string owner, Guid connection, string sender, CancellationToken ct);

    Task<ConversationNote> AddAsync(string owner, Guid connection, string sender, string text, string authorId, string authorName, CancellationToken ct);

    /// <summary>Removes the note. False when there is no such note on that conversation.</summary>
    Task<bool> DeleteAsync(string owner, Guid connection, string sender, string noteId, CancellationToken ct);
}
