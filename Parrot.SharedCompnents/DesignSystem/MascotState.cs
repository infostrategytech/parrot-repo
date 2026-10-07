namespace Parrot.SharedComponents.DesignSystem;

/// <summary>What the <see cref="ParrotMascot"/> is doing, shown in its face and glow.</summary>
public enum MascotState
{
    /// <summary>At rest: breathes, blinks, the glow breathes with it.</summary>
    Idle,

    /// <summary>Hearing the person: leans in, brows up; the glow follows their voice.</summary>
    Listening,

    /// <summary>Working on an answer: looks up and away, three dots.</summary>
    Thinking,

    /// <summary>Talking: the beak moves with the voice.</summary>
    Speaking,

    /// <summary>Something went well: a hop, smiling eyes, a sparkle.</summary>
    Happy
}
