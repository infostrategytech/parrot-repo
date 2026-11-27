namespace Parrot.Application.Agents;

/// <summary>Each workspace's switch for agents sharing a number (temporary: see <see cref="MultiAgentSwitch"/>). Off until set.</summary>
public interface IMultiAgentSwitchStore
{
    Task<bool> IsOnAsync(string workspaceId, CancellationToken cancellationToken);

    Task SetAsync(string workspaceId, bool isOn, string changedBy, CancellationToken cancellationToken);
}
