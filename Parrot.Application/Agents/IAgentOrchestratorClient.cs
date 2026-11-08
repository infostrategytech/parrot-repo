using System.Text.Json;
using Parrot.Application.Conversations;

namespace Parrot.Application.Agents;

/// <summary>The owner's answer to an approval the agent asked for: "approved" or "denied", with anything they want the agent told.</summary>
public sealed record ApprovalDecision(string Decision, string DecidedBy, string? Instruction);

/// <summary>What the orchestrator did with the decision: the turn it resumed, where it ended, and what it told the customer.</summary>
public sealed record ApprovalDecisionResult(string? TurnId, string? Status, string? Reply);

/// <summary>A message typed on a test screen, answered by the owner's live configuration but delivered to no customer.</summary>
public sealed record TestTurnRequest(string ConversationKey, string CustomerRef, string Text, string? CustomerName = null);

public sealed record TestTurnResult(string? TurnId, string? Status, string? Decision, string? Reply, double Confidence);

/// <summary>
/// The AI orchestrator (Omni-AI) as this API calls it, always on an owner's
/// behalf: the token presented names the owner as the tenant. Every failure
/// to reach it or to get an answer is a <see cref="Parrot.Domain.Exceptions.ServiceUnavailableException"/>;
/// what the orchestrator said in its error body never reaches a client.
/// </summary>
public interface IAgentOrchestratorClient
{
    Task<ApprovalDecisionResult> DecideApprovalAsync(string ownerUserId, string approvalId, ApprovalDecision decision, CancellationToken cancellationToken = default);

    /// <summary>A turn's trace, as the orchestrator renders it.</summary>
    Task<JsonElement> GetTraceAsync(string ownerUserId, string traceId, CancellationToken cancellationToken = default);

    /// <summary>The specialists' capability cards.</summary>
    Task<JsonElement> GetRegistryAsync(string ownerUserId, CancellationToken cancellationToken = default);

    Task<TestTurnResult> RunTestTurnAsync(string ownerUserId, TestTurnRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// The live agents covering each channel platform (whatsapp, facebook, instagram, tiktok) for this workspace.
    /// Keyed by platform (lowercase). Returns an empty dictionary when OmniAi is not enabled.
    /// </summary>
    Task<IReadOnlyDictionary<string, ConversationAgentRef>> GetChannelAgentMapAsync(string ownerUserId, CancellationToken cancellationToken = default);
}
