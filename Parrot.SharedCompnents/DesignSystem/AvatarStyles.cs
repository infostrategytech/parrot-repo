namespace Parrot.SharedComponents.DesignSystem;

/// <summary>Original vector artwork and optical alignment, not fake agent data.</summary>
public sealed record AvatarStyle(string Id, PresenceAvatarDirection Direction, string Name, string Description,
    string BodyPath, string FacePosition, PresenceAvatarTone SuggestedTone);

public static class AvatarStyles
{
    // Stable visual IDs; names describe an appearance, never an agent's job or capabilities.
    public static IReadOnlyList<AvatarStyle> All { get; } = Array.AsReadOnly(new AvatarStyle[]
    {
        new("pip", PresenceAvatarDirection.Pip, "Pip", "A soft block with a curious glance.",
            "M39 17h50c13 0 20 8 20 21v50c0 15-9 23-23 23H42c-15 0-23-8-23-23V38c0-13 7-21 20-21Z", "translate(-3 -4)", PresenceAvatarTone.Violet),
        new("bloom", PresenceAvatarDirection.Bloom, "Bloom", "Four gentle lobes and a little playfulness.",
            "M64 30C84 9 117 23 106 49c-3 7-5 11-8 15 21 20 7 53-19 42-7-3-11-5-15-8-20 21-53 7-42-19 3-7 5-11 8-15C9 44 23 11 49 22c7 3 11 5 15 8Z", "translate(-3 -8)", PresenceAvatarTone.Coral),
        new("dash", PresenceAvatarDirection.Dash, "Dash", "A wide capsule with an easy-going gaze.",
            "M41 31h46c19 0 33 14 33 33s-14 33-33 33H41C22 97 8 83 8 64s14-33 33-33Z", "translate(8 8)", PresenceAvatarTone.Green),
        new("pixel", PresenceAvatarDirection.Pixel, "Pixel", "An upright little robot with two soft prongs.",
            "M22 40q0-12 12-12h7V16q0-6 6-6h4q6 0 6 6v12h14V16q0-6 6-6h4q6 0 6 6v12h7q12 0 12 12v55q0 17-17 17H39q-17 0-17-17Z", "translate(0 3)", PresenceAvatarTone.Sky),
        new("dot", PresenceAvatarDirection.Dot, "Dot", "A simple round face, quietly expressive.",
            "M64 16a48 48 0 1 1 0 96 48 48 0 1 1 0-96Z", "translate(0 1)", PresenceAvatarTone.Yellow),
        new("echo", PresenceAvatarDirection.Echo, "Echo", "Two stacked forms with a friendly upper face.",
            "M45 17h38c17 0 29 10 29 23 0 11-8 20-21 23 16 4 25 12 25 25 0 15-13 23-32 23H42c-19 0-32-8-32-23 0-13 9-21 25-25-13-3-21-12-21-23 0-13 12-23 31-23Z", "translate(-7 -17)", PresenceAvatarTone.Pink),
        new("luma", PresenceAvatarDirection.Luma, "Luma", "A soft six-point shape, full of character.",
            "M64 12C74 12 77 26 85 29c9 3 23-2 27 7s-8 18-8 28 12 19 8 28-18 4-27 7c-8 3-11 17-21 17s-13-14-21-17c-9-3-23 2-27-7s8-18 8-28-12-19-8-28 18-4 27-7c8-3 11-17 21-17Z", "translate(0 2)", PresenceAvatarTone.Indigo),
        new("bo", PresenceAvatarDirection.Bo, "Bo", "A rounded robot with broad, friendly sides.",
            "M42 25h44c13 0 20 8 20 20v3h3q10 0 10 10v15q0 10-10 10h-3v2c0 13-7 21-20 21H42c-13 0-20-8-20-21v-2h-3Q9 83 9 73V58q0-10 10-10h3v-3c0-12 7-20 20-20Z", "translate(0 5)", PresenceAvatarTone.Orange),
        new("zig", PresenceAvatarDirection.Zig, "Zig", "A sculpted bow-tie shape with a bright glance.",
            "M25 19h78c6 0 10 4 9 10-3 17-12 27-26 35 14 8 23 18 26 35 1 6-3 10-9 10H25c-6 0-10-4-9-10 3-17 12-27 26-35-14-8-23-18-26-35-1-6 3-10 9-10Z", "translate(0 -17)", PresenceAvatarTone.Blue),
        new("nest", PresenceAvatarDirection.Nest, "Nest", "A gentle dome with a grounded little base.",
            "M64 16c-25 0-39 19-39 43v13L15 87c-5 8 0 18 10 18h78c10 0 15-10 10-18l-10-15V59c0-24-14-43-39-43Z", "translate(0 3)", PresenceAvatarTone.Teal),
        new("ripple", PresenceAvatarDirection.Ripple, "Ripple", "An asymmetric cloud with a low, curious gaze.",
            "M30 48C29 32 42 20 57 20c13 0 23 9 26 20 16-5 31 8 28 24 12 4 14 18 7 28-5 8-14 12-25 12H33c-16 0-25-10-23-23 1-11 8-18 18-20-1-5 0-9 2-13Z", "translate(3 13)", PresenceAvatarTone.Fuchsia),
        new("fig", PresenceAvatarDirection.Fig, "Fig", "A rounded triangle with a steady, friendly face.",
            "M47 24c8-14 26-14 34 0l34 60c8 14-1 29-17 29H30C14 113 5 98 13 84Z", "translate(0 20)", PresenceAvatarTone.Red),
    });

    public static AvatarStyle Of(PresenceAvatarDirection direction) => All.FirstOrDefault(s => s.Direction == direction) ?? All[0];
    public static AvatarStyle Of(string? id) => All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal)) ?? All[0];
}

/// <summary>Appearance only; does not change the agent name, job, voice or permissions.</summary>
public sealed record AvatarAppearance(PresenceAvatarDirection Direction, PresenceAvatarTone Tone)
{
    public static AvatarAppearance Default { get; } = Suggested(PresenceAvatarDirection.Pip);
    public string StyleId => AvatarStyles.Of(Direction).Id;
    public static AvatarAppearance Suggested(PresenceAvatarDirection direction) => new(AvatarStyles.Of(direction).Direction, AvatarStyles.Of(direction).SuggestedTone);
}
