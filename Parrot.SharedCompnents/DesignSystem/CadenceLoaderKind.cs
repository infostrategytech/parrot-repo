namespace Parrot.SharedComponents.DesignSystem;

/// <summary>
/// The seven loaders of the brand document, each for a specific moment.
/// </summary>
public enum CadenceLoaderKind
{
    /// <summary>Default page and panel loader. The mark writes itself bar by bar, then clears. 2.4s loop.</summary>
    Thread,

    /// <summary>An agent is composing a reply: the reply dot stretches into a bar and back. 1.2s loop. Conversations only.</summary>
    Typing,

    /// <summary>Background sync, refreshing data: the three bars nudge forward in sequence. 1.6s loop.</summary>
    Wave,

    /// <summary>Inline and small sizes (16–24px): three slate dots, terracotta passes along them. 1.5s loop.</summary>
    RelayDots,

    /// <summary>Replaces the generic spinner: the reply bar turns in quarter steps around its square corner. 2.4s loop.</summary>
    Turn,

    /// <summary>Determinate: a slate track with a terracotta fill that keeps the square corner at its start.</summary>
    Progress,

    /// <summary>App launch and splash: the mark builds in conversation order and ends on the static logo. 3s, plays once.</summary>
    Assemble
}
