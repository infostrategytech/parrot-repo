namespace Parrot.Application.Contracts.Conversations;

/// <summary>
/// The agent has the answer. AgentReplies: it answers the customer itself; false when a person has taken over the
/// conversation, and the reply is theirs to send.
/// </summary>
public sealed record QuestionAnswerResponse(bool AgentReplies);
