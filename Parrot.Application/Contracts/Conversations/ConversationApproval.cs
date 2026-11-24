namespace Parrot.Application.Contracts.Conversations;

/// <summary>
/// An action an agent asked the team to approve in a conversation, still waiting for a decision. Tool and
/// Reason are what the orchestrator sent, and may be absent.
/// </summary>
public sealed record ConversationApproval(string ApprovalId, string? Tool, string? Reason);
