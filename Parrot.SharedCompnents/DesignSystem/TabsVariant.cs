namespace Parrot.SharedComponents.DesignSystem;

/// <summary>How a <see cref="Tabs"/> set looks.</summary>
public enum TabsVariant
{
    /// <summary>A muted tray whose tabs share its width; the chosen one is a raised card. For filters over a list.</summary>
    Segmented,

    /// <summary>A card pill floating over content; the chosen tab is tinted. For switching what a pane shows.</summary>
    Floating,

    /// <summary>No tray; the chosen tab is filled. For a mode inside a card, such as a composer.</summary>
    Flat,

    /// <summary>
    /// A row with a hairline under it; the chosen tab is underlined. For a
    /// page's sections, such as an agent's settings (designer, 14 October
    /// 2026). On a narrow screen the row scrolls sideways.
    /// </summary>
    Underline,

    /// <summary>
    /// A page's views, as Products &amp; Services and Settings draw them (designer, 20 October 2026): a
    /// darker tray on the page, tabs as wide as their words, the chosen one a raised card in full-strength
    /// text, counts as small chips that take the brand colour when chosen. Put the label in a Typography.
    /// </summary>
    Tray
}
