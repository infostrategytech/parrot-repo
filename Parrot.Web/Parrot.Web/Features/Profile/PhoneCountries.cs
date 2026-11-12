using Parrot.SharedComponents.DesignSystem;

namespace Parrot.Web.Features.Profile;

/// <summary>A country a person's number can be from: its name and dial code without the plus.</summary>
public sealed record PhoneCountry(string Name, string Code)
{
    public string Label => $"{Name} +{Code}";
}

/// <summary>
/// The person's own number, as the profile API keeps it: a dial code and the national number, saved together
/// (a plus in the number has to match the code). Nigeria first; the API takes any code, these are the ones
/// yára's sellers and their teams use.
/// </summary>
public static class PhoneCountries
{
    public static IReadOnlyList<PhoneCountry> All { get; } =
    [
        new("Nigeria", "234"), new("Ghana", "233"), new("Kenya", "254"), new("South Africa", "27"),
        new("United Kingdom", "44"), new("United States & Canada", "1"),
    ];

    public static PhoneCountry Home => All[0];

    // The API accepts countries outside this short picker. Do not guess a dial code for a saved number:
    // keep it intact and editable only after the person chooses a supported country.
    public static PhoneCountry Unlisted { get; } = new("Other country", "");

    /// <summary>A saved number (+234 803 555 1234) as its country and the number to show, or Nigeria and nothing.</summary>
    public static (PhoneCountry Country, string Number) Split(string? saved)
    {
        var digits = Digits(saved);
        if (digits.Length == 0) return (Home, string.Empty);
        // Longest code first, so +1 never takes a longer one's place.
        foreach (var country in All.OrderByDescending(country => country.Code.Length))
            if (digits.StartsWith(country.Code, StringComparison.Ordinal)) return (country, Show(country, digits[country.Code.Length..]));
        return (Unlisted, saved!.Trim());
    }

    /// <summary>What's wrong with a number for its country, or null. A Nigerian one is checked as everywhere in yára.</summary>
    public static string? Problem(PhoneCountry country, string? typed)
    {
        var text = (typed ?? string.Empty).Trim();
        if (text.Length == 0) return null;
        if (country == Unlisted) return "Choose a country before changing this number.";
        // A plus names the country, so it has to be the one chosen: the API refuses it otherwise.
        if (text.StartsWith('+') && !Digits(text).StartsWith(country.Code, StringComparison.Ordinal))
            return "That number has another country's code. Choose its country first.";
        if (country.Code == ContactText.HomeCode) return ContactText.Phone(text).Advice;
        var national = National(country, text);
        return national.Length is < 6 or > 14 || text.Any(c => !char.IsAsciiDigit(c) && c is not (' ' or '-' or '(' or ')' or '+'))
            ? "Use numbers only, such as 20 7946 0958."
            : null;
    }

    /// <summary>The number as the API takes it: the national digits, without the country code or a 0 in front.</summary>
    public static string National(PhoneCountry country, string typed)
    {
        var digits = Digits(typed);
        if (typed.TrimStart().StartsWith('+') && digits.StartsWith(country.Code, StringComparison.Ordinal)) digits = digits[country.Code.Length..];
        return digits.TrimStart('0');
    }

    /// <summary>The same number, however it was written.</summary>
    public static bool Same(PhoneCountry a, string? typedA, PhoneCountry b, string? typedB) =>
        (Digits(typedA).Length == 0 && Digits(typedB).Length == 0)
        || (a.Code == b.Code && National(a, typedA ?? string.Empty) == National(b, typedB ?? string.Empty));

    // Nigerian numbers as people write them, with the 0: 0803 555 1234.
    private static string Show(PhoneCountry country, string national) =>
        country.Code == ContactText.HomeCode && national.Length == 10 ? $"0{national[..3]} {national[3..6]} {national[6..]}" : national;

    private static string Digits(string? text) => new((text ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
}
