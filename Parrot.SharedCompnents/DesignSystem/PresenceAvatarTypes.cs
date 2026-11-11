namespace Parrot.SharedComponents.DesignSystem;

/// <summary>Exploratory character constructions; not assigned to live agents.</summary>
public enum PresenceAvatarDirection { Pip, Bloom, Dash, Pixel, Dot, Echo, Luma, Bo, Zig, Nest, Ripple, Fig }

/// <summary>Display state supplied by the caller, never inferred from an animation.</summary>
public enum PresenceAvatarState { Idle, Thinking, Working, Waiting, Blocked, Done, Listening, Speaking }

/// <summary>Bright, vivid colours for self-expression (designer, 9 November 2026: not limited to the brand).</summary>
public enum PresenceAvatarTone { Violet, Coral, Green, Sky, Amber, Red, Orange, Yellow, Lime, Teal, Blue, Indigo, Fuchsia, Pink }

public static class PresenceAvatarTones
{
    /// <summary>The picker's order: round the colour wheel, so neighbours feel related.</summary>
    public static IReadOnlyList<PresenceAvatarTone> Palette { get; } = Array.AsReadOnly(new[]
    {
        PresenceAvatarTone.Red, PresenceAvatarTone.Coral, PresenceAvatarTone.Orange, PresenceAvatarTone.Amber,
        PresenceAvatarTone.Yellow, PresenceAvatarTone.Lime, PresenceAvatarTone.Green, PresenceAvatarTone.Teal,
        PresenceAvatarTone.Sky, PresenceAvatarTone.Blue, PresenceAvatarTone.Indigo, PresenceAvatarTone.Violet,
        PresenceAvatarTone.Fuchsia, PresenceAvatarTone.Pink,
    });
}

/// <summary>Small 32px, Medium 48px, Large 184px; Fill takes the size of the box it's placed in (a screen's own face size).</summary>
public enum PresenceAvatarSize { Small, Medium, Large, Fill }

public static class PresenceAvatarWords
{
    public static string Label(PresenceAvatarState state) => state switch
    {
        PresenceAvatarState.Idle => "Ready",
        PresenceAvatarState.Thinking => "Thinking",
        PresenceAvatarState.Working => "Working",
        PresenceAvatarState.Waiting => "Waiting for you",
        PresenceAvatarState.Blocked => "Needs help",
        PresenceAvatarState.Done => "Done",
        PresenceAvatarState.Listening => "Listening",
        PresenceAvatarState.Speaking => "Speaking",
        _ => "Status unavailable",
    };

    public static string Description(PresenceAvatarState state) => state switch
    {
        PresenceAvatarState.Idle => "A quiet blink. Present, without asking for attention.",
        PresenceAvatarState.Thinking => "A thoughtful glance while it works out the next step.",
        PresenceAvatarState.Working => "Small, purposeful movement while a task is in progress.",
        PresenceAvatarState.Waiting => "An attentive tilt. It's your turn to respond.",
        PresenceAvatarState.Blocked => "A still, concerned expression. Something needs your help.",
        PresenceAvatarState.Done => "A happy turn, then it settles. No endless celebration.",
        PresenceAvatarState.Listening => "An open, attentive expression while it hears you.",
        PresenceAvatarState.Speaking => "A gentle rhythm and expressive eyes while it replies.",
        _ => "The current activity isn't available.",
    };

    public static PresenceAvatarState FromVoice(VoiceState state) => state switch
    {
        VoiceState.Listening => PresenceAvatarState.Listening,
        VoiceState.Thinking => PresenceAvatarState.Thinking,
        VoiceState.Speaking => PresenceAvatarState.Speaking,
        _ => PresenceAvatarState.Idle,
    };
}
