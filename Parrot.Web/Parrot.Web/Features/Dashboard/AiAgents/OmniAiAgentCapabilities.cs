namespace Parrot.Web.Features.Dashboard.AiAgents;

/// <summary>
/// With the orchestrator connected (OmniAi:Enabled): a turned-on agent
/// really answers on its channels, so the screens offer the switch.
/// </summary>
public sealed class OmniAiAgentCapabilities(IConfiguration configuration) : IAgentCapabilities
{
    public bool CanReply => configuration.GetValue<bool>("OmniAi:Enabled");
}
