namespace Parrot.Web.Features.Dashboard.Workspace;

/// <summary>
/// Owns the workspace welcome dialog so first-time setup and adding another
/// workspace use one experience from anywhere in the dashboard.
/// </summary>
public sealed class WorkspaceCreationState
{
    public bool IsOpen { get; private set; }
    public bool IsAdditionalWorkspace { get; private set; }

    public event Action? Changed;

    public void EnsureFirstWorkspace(bool needed)
    {
        if (!needed || IsOpen) return;
        IsAdditionalWorkspace = false;
        IsOpen = true;
        Changed?.Invoke();
    }

    public void OpenAdditionalWorkspace()
    {
        IsAdditionalWorkspace = true;
        IsOpen = true;
        Changed?.Invoke();
    }

    public void Close()
    {
        IsOpen = false;
        IsAdditionalWorkspace = false;
        Changed?.Invoke();
    }
}
