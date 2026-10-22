namespace Parrot.Web.Features.Dashboard.AiAgents;

/// <summary>
/// Which agents have been tested as a customer (a reply seen), for "Get Tolu ready": once tested, turning it on
/// is the next step. Kept for the session; the backend could keep it (agents-backend-requests).
/// </summary>
public sealed class AgentReadiness
{
    private readonly HashSet<string> _tested = [];

    public event Action? Changed;

    public bool Tested(string agentId) => _tested.Contains(agentId);

    public void MarkTested(string agentId)
    {
        if (_tested.Add(agentId)) Changed?.Invoke();
    }
}
