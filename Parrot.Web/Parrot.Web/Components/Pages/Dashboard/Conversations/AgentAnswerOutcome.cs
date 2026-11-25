namespace Parrot.Web.Components.Pages.Dashboard.Conversations;

/// <summary>What the Answer dialog promises before an answer goes to the agent, and what it says once it has.</summary>
public static class AgentAnswerOutcome
{
    /// <summary>
    /// The line under the answer box. While a person is replying in the chat the agent won't message the customer,
    /// so they are told to send the reply themselves.
    /// </summary>
    public static string Commitment(string agent, string customer, bool personReplying) => personReplying
        ? $"{Capitalised(agent)} gets your answer but won't message {customer} while you're replying here. Send your reply yourself."
        : $"{Capitalised(agent)} uses your answer to reply to {customer}.";

    /// <summary>The toast once the agent has the answer, by whether it replies to the customer and whether it was saved.</summary>
    public static (string Title, string Description) Sent(string agent, string customer, bool agentReplies, bool saved)
    {
        string reply = agentReplies
            ? $"{Capitalised(agent)} will reply to {customer}."
            : $"Reply to {customer} yourself.";
        return ($"Sent to {agent}.", saved ? $"{reply} Saved for next time." : reply);
    }

    private static string Capitalised(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
