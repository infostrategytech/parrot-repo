using System.Globalization;
using System.Text.RegularExpressions;

namespace Parrot.SharedComponents.DesignSystem;

/// <summary>Strict number entry, not caption extraction. Never strip a sign, letters or extra decimals.
/// Commas must be valid thousands separators; a leading decimal point means a fraction, not whole units.</summary>
public static partial class NumberText
{
    public static decimal? Decimal(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        return DecimalPattern().IsMatch(value)
            && decimal.TryParse(value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var number)
            ? number : null;
    }

    public static int? Integer(string? text, int maximum = int.MaxValue)
    {
        var value = (text ?? string.Empty).Trim();
        return IntegerPattern().IsMatch(value)
            && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number <= maximum
            ? number : null;
    }

    public static string? Grouped(string? text) => Decimal(text) is { } number
        ? number.ToString("#,0.##", CultureInfo.InvariantCulture) : text;

    [GeneratedRegex(@"\A(?:(?:[0-9]+|[0-9]{1,3}(?:,[0-9]{3})+)(?:\.[0-9]{1,2})?|\.[0-9]{1,2})\z")]
    private static partial Regex DecimalPattern();

    [GeneratedRegex(@"\A[0-9]+\z")]
    private static partial Regex IntegerPattern();
}
