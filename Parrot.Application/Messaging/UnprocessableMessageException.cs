namespace Parrot.Application.Messaging;

/// <summary>
/// A message that will never succeed however often it is retried: the body
/// can't be decoded, a required field is missing, or the source is unknown.
/// The worker sends it to the dead-letter topic and moves on, instead of
/// retrying it and holding up its partition.
///
/// Anything else (MongoDB, PostgreSQL or Kafka down, a timeout) is treated as
/// temporary and retried.
/// </summary>
public sealed class UnprocessableMessageException(string reason, Exception? inner = null)
    : Exception(reason, inner)
{
    /// <summary>Short and free of message content: it goes in a dead-letter header and the logs.</summary>
    public string Reason { get; } = reason;
}
