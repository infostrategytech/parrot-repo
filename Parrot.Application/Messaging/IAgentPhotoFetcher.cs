using Parrot.Application.Conversations;

namespace Parrot.Application.Messaging;

/// <summary>
/// Downloads a photo the AI wants to send, so it can go out the way a teammate's picture does
/// (the channels take the file itself, and the inbox keeps its own copy).
/// </summary>
public interface IAgentPhotoFetcher
{
    /// <summary>The photo as a JPEG or PNG of at most <see cref="ConversationService.MaxImageBytes"/>.</summary>
    /// <exception cref="AgentPhotoRejectedException">Not a public https link, not reachable, too big, or not a JPEG or PNG.</exception>
    Task<OutgoingFile> FetchAsync(string url, CancellationToken ct);
}

/// <summary>A photo that can't be sent. The message says why, for the logs.</summary>
public sealed class AgentPhotoRejectedException(string message, Exception? inner = null) : Exception(message, inner);
