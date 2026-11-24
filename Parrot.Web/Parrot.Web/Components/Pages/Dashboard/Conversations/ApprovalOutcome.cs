using Parrot.Application.Agents;

namespace Parrot.Web.Components.Pages.Dashboard.Conversations;

/// <summary>What the approval card says once a decision has gone, by what came back.</summary>
public static class ApprovalOutcome
{
    /// <summary>The approval had stopped waiting (decided, or expired) before the decision reached it.</summary>
    public const string Gone = "gone";

    /// <summary>Someone else decided it first.</summary>
    public const string DecidedElsewhere = "decided-elsewhere";

    /// <summary>The card's state after a decision: "approved" or "denied" as asked, or <see cref="DecidedElsewhere"/>.</summary>
    public static string State(string decision, string? status) =>
        status == ApprovalDecisionStatus.AlreadyDecided ? DecidedElsewhere : decision;

    /// <summary>The card's heading and line for a state, with the answer's status and the agent that asked.</summary>
    public static (string Title, string Note) Describe(string state, string? status, string? agentName)
    {
        string agent = string.IsNullOrWhiteSpace(agentName) ? "the agent" : agentName;
        string agentAtStart = string.IsNullOrWhiteSpace(agentName) ? "The agent" : agentName;
        return state switch
        {
            Gone => ("No longer waiting", "It was decided, or it expired, before your decision reached it. Nothing was changed."),
            DecidedElsewhere => ("Already decided", "Someone else decided this first. Nothing was changed."),
            _ => (state == "approved" ? "Approved" : "Denied", status switch
            {
                ApprovalDecisionStatus.NoTurn => $"Decision recorded, but {agent} had already moved on, so nothing more happens in this chat.",
                ApprovalDecisionStatus.ConversationBusy => $"Decision recorded. {agentAtStart} is replying here right now and will act on it in a moment.",
                _ => $"Decision sent. Waiting for {agent} to continue.",
            }),
        };
    }
}
