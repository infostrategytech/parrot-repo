namespace Parrot.SharedComponents.DesignSystem;

/// <summary>
/// The letters an avatar shows for a name. Two at most: the first letter of
/// the first two words, or the first two letters of a single word. An email
/// address counts as a single word — the part before the @.
/// </summary>
public static class Initials
{
    public static string From(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";

        var local = name.Trim();
        var at = local.IndexOf('@');
        if (at > 0) local = local[..at];

        var words = local.Split([' ', '.', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "";
        if (words.Length == 1)
        {
            return words[0].Length >= 2
                ? words[0][..2].ToUpperInvariant()
                : words[0].ToUpperInvariant();
        }
        return string.Concat(words[0][0], words[1][0]).ToUpperInvariant();
    }
}
