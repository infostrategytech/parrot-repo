namespace Parrot.SharedComponents.DesignSystem;

/// <summary>
/// Soft tints for initials avatars. The colour is derived from a stable seed —
/// a customer or teammate identifier — so the same person keeps the same tint
/// everywhere they appear rather than changing on every render.
/// </summary>
public static class Petal
{
    public const int Count = 10;

    /// <summary>
    /// A direct port of lib/petal.ts. The arithmetic has to match exactly or
    /// every avatar in the app changes colour between the two implementations.
    ///
    /// JavaScript's <c>(hash * 31 + charCode) &gt;&gt;&gt; 0</c> coerces to an
    /// unsigned 32-bit integer, which is what C# <c>uint</c> does on overflow,
    /// and <c>charCodeAt</c> returns a UTF-16 code unit, which is what C#
    /// <c>char</c> is. Same inputs, same output, byte for byte.
    /// </summary>
    public static int IndexFor(string seed)
    {
        uint hash = 0;
        foreach (var unit in seed)
        {
            unchecked
            {
                hash = hash * 31 + unit;
            }
        }
        return (int)(hash % Count);
    }

    public static string ClassFor(string seed) => "petal-" + IndexFor(seed);
}
