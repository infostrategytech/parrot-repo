namespace Parrot.Web.Features.Dashboard.AiAgents;

/// <summary>
/// What agents can do with the backend that is running. The AI Operations
/// screens read only this to decide whether an agent can be turned on; when
/// the API can make a turned-on agent reply, it is switched on here and the
/// screens follow.
/// </summary>
public interface IAgentCapabilities
{
    /// <summary>
    /// An agent can be turned on and reply to customers. Without it, agents are
    /// set up and saved as drafts, and nothing says one is replying.
    /// </summary>
    bool CanReply { get; }
}

/// <summary>
/// With the real backend today: no link yet from an agent to its channels'
/// replies, so a turned-on agent would answer no one while the screens said
/// it did (see agents-backend-requests). Agents are saved as drafts.
/// </summary>
public sealed class LiveAgentCapabilities : IAgentCapabilities
{
    public bool CanReply => false;
}
