namespace Parrot.Infrastructure.Conversations;

internal static class MetaReplyTo
{
    // Messenger's and Instagram's quote: reply_to.mid at the top of the send body, added only for a reply that
    // quotes a message. The serializer's ignore-nulls setting does not apply to a dictionary's entries.
    public static Dictionary<string, object?> Quoting(this Dictionary<string, object?> body, string? replyTo)
    {
        if (replyTo is not null) body["reply_to"] = new { mid = replyTo };
        return body;
    }
}
