namespace Parrot.Application.Messaging;

/// <summary>
/// Download links for saved files that work without signing in, for readers outside the inbox: the AI team
/// downloads the files in <see cref="MessagePersisted"/> with them. Each link opens one file and stops working
/// after a week. The inbox keeps its own shorter links.
/// </summary>
public interface IMediaLinkSigner
{
    /// <summary>The file's full download address, or null when no link can be made (no public address set, or signing failed).</summary>
    string? Sign(string storageKey, string fileName);
}
