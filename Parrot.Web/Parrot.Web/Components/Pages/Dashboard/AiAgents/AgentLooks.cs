using Parrot.SharedComponents.DesignSystem;

namespace Parrot.Web.Components.Pages.Dashboard.AiAgents;

/// <summary>
/// An agent's look (designer, 10 November 2026): one of the twelve characters in any bright colour,
/// replacing the six picture mascots. It is saved in the agent's existing look field as short text,
/// "look-pip-violet", the same kind of value the AI service already keeps ("Nova"). An agent saved
/// with a picture mascot gets the character in that picture's background colour, so it keeps a look
/// of its own. Backend confirmation is requested in the agents backend request doc.
/// </summary>
public static class AgentLooks
{
    // Saved as "look-pip-violet": the AI service keeps a look key of letters, digits, '-' and '_' only (12 November
    // 2026). Looks saved earlier as "look:pip:violet" still read.
    private const string Prefix = "look-";
    private const string OldPrefix = "look:";

    // Each old picture's background colour, on its own shape.
    private static readonly IReadOnlyDictionary<string, AvatarAppearance> Pictures = new Dictionary<string, AvatarAppearance>(StringComparer.OrdinalIgnoreCase)
    {
        ["Nova"] = new(PresenceAvatarDirection.Pip, PresenceAvatarTone.Yellow),
        ["Atlas"] = new(PresenceAvatarDirection.Bloom, PresenceAvatarTone.Violet),
        ["Bolt"] = new(PresenceAvatarDirection.Dot, PresenceAvatarTone.Indigo),
        ["Aegis"] = new(PresenceAvatarDirection.Luma, PresenceAvatarTone.Fuchsia),
        ["Buzz"] = new(PresenceAvatarDirection.Echo, PresenceAvatarTone.Pink),
        ["Luna"] = new(PresenceAvatarDirection.Dash, PresenceAvatarTone.Lime),
    };

    public static bool IsLook(string? value) =>
        value?.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) == true || value?.StartsWith(OldPrefix, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The character and colour for a saved look, an old mascot's name, or nothing (the default).</summary>
    public static AvatarAppearance Of(string? value)
    {
        if (IsLook(value))
        {
            var parts = value![Prefix.Length..].Split('-', ':');
            var style = AvatarStyles.All.FirstOrDefault(s => string.Equals(s.Id, parts[0], StringComparison.OrdinalIgnoreCase));
            if (style is not null)
                return new(style.Direction, parts.Length > 1 && Enum.TryParse<PresenceAvatarTone>(parts[1], true, out var tone) ? tone : style.SuggestedTone);
        }
        return value is not null && Pictures.TryGetValue(value, out var picture) ? picture : AvatarAppearance.Default;
    }

    public static string Save(AvatarAppearance look) => $"{Prefix}{look.StyleId}-{look.Tone.ToString().ToLowerInvariant()}";

    /// <summary>A new agent's look: the first character, in its own colour, that no other agent wears.</summary>
    public static string Free(IEnumerable<string?> taken)
    {
        var worn = taken.Select(Of).Select(a => a.Direction).ToHashSet();
        var style = AvatarStyles.All.FirstOrDefault(s => !worn.Contains(s.Direction)) ?? AvatarStyles.All[0];
        return Save(AvatarAppearance.Suggested(style.Direction));
    }
}
