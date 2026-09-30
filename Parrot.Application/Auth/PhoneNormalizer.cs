namespace Parrot.Application.Auth;

/// <summary>
/// Two rules and nothing else. The setup screen is handed the country, so it is
/// known rather than worked out; sign-in is handed one string, and that string
/// is compared against the stored columns instead of being split. There is no
/// dial-code table here and no guessing about where a country code ends — that
/// boundary is never needed.
/// </summary>
public static class PhoneNormalizer
{
    private const int MaxDigits = 15; // E.164's ceiling.

    /// <summary>
    /// Setup side. The country arrives alongside the number, so it is stated,
    /// not inferred. Returns the national digits to store, or null when the
    /// pair cannot describe a phone number.
    /// </summary>
    public static string? ToNational(string? number, string? dialCode)
    {
        string country = Digits(dialCode);
        if (country.Length is < 1 or > 3 || country[0] == '0')
        {
            return null;
        }

        string typed = Digits(number);
        string national;

        // The form may send the number in any shape a person would type it.
        // Each one reduces to the same national part, using the country the
        // form already gave us.
        if (typed.StartsWith(country, StringComparison.Ordinal))
        {
            national = typed[country.Length..];
        }
        else if (typed.Length > 1 && typed[0] == '0')
        {
            national = typed[1..];
        }
        else
        {
            national = typed;
        }

        // Short enough to be a number, and short enough that the country and
        // the national part still fit the standard's ceiling together.
        return national.Length >= 2 && national.Length + country.Length <= MaxDigits
            ? national
            : null;
    }

    /// <summary>
    /// Sign-in side. Returns the single value to search on, or null when the
    /// input cannot be a phone number. A leading zero is the local format, so
    /// it comes off; anything else is left whole, ready to be compared against
    /// the two stored columns joined.
    /// </summary>
    public static string? SearchValue(string? identifier)
    {
        string digits = Digits(identifier);
        if (digits.Length is < 2 or > MaxDigits)
        {
            return null;
        }

        if (digits[0] != '0')
        {
            return digits;
        }

        string national = digits[1..];
        return national.Length >= 2 ? national : null;
    }

    /// <summary>Rebuilds the display form, e.g. +2348012345678, from the two stored columns.</summary>
    public static string? ToDisplay(string? dialCode, string? nationalNumber)
    {
        if (string.IsNullOrWhiteSpace(dialCode) || string.IsNullOrWhiteSpace(nationalNumber))
        {
            return null;
        }

        string digits = Digits(dialCode + nationalNumber);
        return digits.Length > dialCode.Length ? $"+{digits}" : null;
    }

    private static string Digits(string? raw) =>
        raw is null ? string.Empty : new(raw.Where(char.IsAsciiDigit).ToArray());
}
