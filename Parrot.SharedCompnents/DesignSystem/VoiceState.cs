namespace Parrot.SharedComponents.DesignSystem;

/// <summary>What the voice agent is doing, as a <see cref="VoiceOrb"/> and a <see cref="VoiceAura"/> show it.</summary>
public enum VoiceState
{
    /// <summary>At rest: breathing slowly.</summary>
    Idle,
    /// <summary>Hearing the person: the orb swells and quickens with their voice.</summary>
    Listening,
    /// <summary>Working out a reply: the colours swirl fast.</summary>
    Thinking,
    /// <summary>The agent talking: the orb swells and brightens with its voice.</summary>
    Speaking
}
