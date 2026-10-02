namespace Parrot.Application.Messaging;

/// <summary>
/// Hands out each conversation's next <see cref="StoredMessage.Sequence"/>.
///
/// Numbers increase in the order they are taken. Kafka delivers one
/// conversation's messages in order and the worker handles them one at a
/// time, so the sequence follows message order. A number taken by a write
/// that then fails is never reused, so gaps are normal.
/// </summary>
public interface IConversationSequence
{
    Task<long> NextAsync(string conversationKey, CancellationToken cancellationToken = default);
}
