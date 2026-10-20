using Parrot.SharedComponents.Layout;
using Parrot.Web.Features.Auth.Models;

namespace Parrot.Web.Features.Dashboard.Workspace;

/// <summary>
/// The frontend's active workspace and switcher list. The demo keeps added
/// workspaces here for the current signed-in session; the production adapter
/// will replace these operations with the workspace API.
/// </summary>
public sealed class WorkspaceDirectoryState
{
    private readonly List<WorkspaceMenuItem> _workspaces = [];
    private readonly HashSet<string> _locallyCreated = [];
    private string? _userId;

    public IReadOnlyList<WorkspaceMenuItem> Workspaces => _workspaces;
    public WorkspaceMenuItem? Active { get; private set; }
    public bool IsActiveLocallyCreated => Active is not null && _locallyCreated.Contains(Active.Id);

    public event Action? Changed;

    public void Sync(AuthResponse? user)
    {
        if (user is null)
        {
            _userId = null;
            _workspaces.Clear();
            _locallyCreated.Clear();
            Active = null;
            Changed?.Invoke();
            return;
        }

        if (_userId != user.UserId)
        {
            _userId = user.UserId;
            _workspaces.Clear();
            _locallyCreated.Clear();
            Active = null;
        }

        if (string.IsNullOrWhiteSpace(user.BusinessName)) return;

        var primaryId = $"workspace-{user.UserId}-primary";
        var primary = new WorkspaceMenuItem(primaryId, user.BusinessName, LogoUrl: user.LogoUrl);
        var index = _workspaces.FindIndex(workspace => workspace.Id == primaryId);
        if (index < 0) _workspaces.Insert(0, primary);
        else _workspaces[index] = primary;

        Active ??= primary;
        if (Active.Id == primaryId) Active = primary;
        Changed?.Invoke();
    }

    public void AddLocal(string name, string? logoUrl)
    {
        var workspace = new WorkspaceMenuItem($"workspace-{Guid.NewGuid():N}", name, LogoUrl: logoUrl);
        _workspaces.Add(workspace);
        _locallyCreated.Add(workspace.Id);
        Active = workspace;
        Changed?.Invoke();
    }

    public bool Select(string id)
    {
        var workspace = _workspaces.FirstOrDefault(item => item.Id == id);
        if (workspace is null || workspace.Id == Active?.Id) return false;
        Active = workspace;
        Changed?.Invoke();
        return true;
    }
}
