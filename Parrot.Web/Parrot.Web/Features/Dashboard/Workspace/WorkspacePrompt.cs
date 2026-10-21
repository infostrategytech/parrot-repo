namespace Parrot.Web.Features.Dashboard.Workspace;

/// <summary>
/// How the switcher asks for the workspace card to open for a new workspace.
/// The card takes no parameters (see WorkspaceWelcome), so the request comes
/// through this circuit-scoped event instead.
/// </summary>
public sealed class WorkspacePrompt
{
    public event Action? CreateRequested;

    public void RequestCreate() => CreateRequested?.Invoke();
}
