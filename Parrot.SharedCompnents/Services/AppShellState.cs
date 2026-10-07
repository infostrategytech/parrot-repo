using Microsoft.JSInterop;

namespace Parrot.SharedComponents.Services;

/// <summary>
/// The navigation rail's collapsed state.
///
/// This is a service rather than component state because React learned the
/// hard way that it could not live on a page: every page rendered its own
/// sidebar, so collapsing the rail and then switching view re-mounted it in
/// the default state. React solved it by hoisting the flag into App and
/// drilling it through six pages; here one object is injected by the two
/// components that care.
/// </summary>
public sealed class AppShellState
{
    /// <summary>Below this the rail collapses on its own. Matches React's behaviour.</summary>
    public const string NarrowQuery = "(max-width: 1180px)";

    private bool _collapsedByViewport;
    // The compact rail is the product's default. A person may still expand it
    // for the current session; narrow viewports continue to begin compact.
    private bool? _collapsedByPerson = true;

    /// <summary>
    /// A deliberate collapse outranks the viewport: if someone collapses the
    /// rail on a wide screen it stays collapsed.
    /// </summary>
    public bool Collapsed => _collapsedByPerson ?? _collapsedByViewport;

    public event Action? Changed;

    public void Toggle()
    {
        _collapsedByPerson = !Collapsed;
        Changed?.Invoke();
    }

    public void Collapse()
    {
        if (Collapsed)
            return;

        _collapsedByPerson = true;
        Changed?.Invoke();
    }

    /// <summary>Called from the media-query observer in aurora-interop.js.</summary>
    [JSInvokable]
    public void SetViewportNarrow(bool narrow)
    {
        if (_collapsedByViewport == narrow) return;
        _collapsedByViewport = narrow;
        if (_collapsedByPerson is null) Changed?.Invoke();
    }
}
