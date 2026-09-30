namespace Parrot.SharedComponents.DesignSystem;

/// <summary>How a <see cref="Tabs"/> set looks.</summary>
public enum TabsVariant
{
    /// <summary>A muted tray whose tabs share its width; the chosen one is a raised card. For filters over a list.</summary>
    Segmented,

    /// <summary>A card pill floating over content; the chosen tab is tinted. For switching what a pane shows.</summary>
    Floating,

    /// <summary>No tray; the chosen tab is filled. For a mode inside a card, such as a composer.</summary>
    Flat
}
