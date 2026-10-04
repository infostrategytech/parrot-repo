using System.Collections.Concurrent;
using System.Text;

namespace Parrot.Catalog.Domain.Enums;

/// <summary>
/// The one place that spells an enum member for the database and the wire:
/// PascalCase in C#, snake_case everywhere else (BundledWith -> bundled_with).
/// </summary>
public static class EnumNames
{
    private static readonly ConcurrentDictionary<(Type, string), object> Parsed = new();

    public static string ToSnake<TEnum>(TEnum value)
        where TEnum : struct, Enum
        => ToSnake(value.ToString());

    public static string ToSnake(string pascal)
    {
        StringBuilder sb = new StringBuilder(pascal.Length + 4);
        for (int i = 0; i < pascal.Length; i++)
        {
            char c = pascal[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    sb.Append('_');
                }

                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    public static bool TryParse<TEnum>(string? snake, out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;
        if (string.IsNullOrEmpty(snake))
        {
            return false;
        }

        if (Parsed.TryGetValue((typeof(TEnum), snake), out object? cached))
        {
            value = (TEnum)cached;
            return true;
        }

        foreach (TEnum candidate in Enum.GetValues<TEnum>())
        {
            if (string.Equals(ToSnake(candidate), snake, StringComparison.Ordinal))
            {
                Parsed[(typeof(TEnum), snake)] = candidate;
                value = candidate;
                return true;
            }
        }

        return false;
    }

    public static IReadOnlyList<string> All<TEnum>()
        where TEnum : struct, Enum
        => Enum.GetValues<TEnum>().Select(ToSnake).ToArray();
}
