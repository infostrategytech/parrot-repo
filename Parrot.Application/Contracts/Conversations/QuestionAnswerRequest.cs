namespace Parrot.Application.Contracts.Conversations;

/// <summary>
/// A person's answer to an agent's question in a conversation. SaveForNextTime: the agent keeps it for every
/// customer, not only this one.
/// </summary>
public sealed record QuestionAnswerRequest(string? Answer, bool SaveForNextTime);
