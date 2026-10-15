namespace Parrot.Domain.Entities.Messages;

/// <summary>
/// Where a file stands on its way into private storage. Kept on the attachment so the inbox can say
/// "still loading" or "could not be saved" instead of guessing from a missing key.
/// </summary>
public static class MediaStatus
{
    /// <summary>The message is stored; the copy of the file is not made yet.</summary>
    public const string Pending = "pending";

    /// <summary>The private copy exists and can be read.</summary>
    public const string Ready = "ready";

    /// <summary>The copy could not be made (the platform refused it, the link expired, storage was down).</summary>
    public const string Failed = "failed";

    /// <summary>Bigger than the limit, so it was never copied.</summary>
    public const string TooLarge = "too_large";

    /// <summary>
    /// The attachment's status. Messages stored before the field existed carry none, so it is read from the
    /// copy: a saved key (or a link, which has nothing to save) is ready, anything else never was.
    /// </summary>
    public static string Of(MessageAttachment attachment) =>
        attachment.Status ?? (attachment.StorageKey is not null || attachment.Kind == "link" ? Ready : Failed);
}

/// <summary>The reason codes kept in <see cref="MessageAttachment.FailureReason"/>.</summary>
public static class MediaFailure
{
    /// <summary>The platform said no (expired, not allowed). Asking again will not change it.</summary>
    public const string Refused = "refused";

    /// <summary>The platform was busy or failing (5xx, 429). A later try may work.</summary>
    public const string Unavailable = "unavailable";

    public const string Timeout = "timeout";

    /// <summary>The file arrived with no bytes.</summary>
    public const string Empty = "empty";

    /// <summary>Anything else: no token, no address, storage down.</summary>
    public const string Error = "error";

    /// <summary>Whether another try could change the outcome.</summary>
    public static bool IsWorthRetrying(string? reason) => reason is Unavailable or Timeout or Error;
}
