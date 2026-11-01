using System.Text.RegularExpressions;

namespace Parrot.SharedComponents.DesignSystem;

/// <summary>
/// A phone number, read: one shape (+ and digits) to save, or what's wrong with it. <paramref name="Problem"/>
/// is a short label for a list ("Number too short"); <paramref name="Advice"/> is the sentence under a field
/// that says how to fix it.
/// </summary>
/// <param name="Local">Written as a local number (0803…): the country code was added.</param>
public sealed record PhoneCheck(string? Number, string? Problem, string? Advice, bool Local = false);

/// <summary>
/// Phone numbers and emails, checked one way on every screen (designer, 28 October 2026: "for phone numbers
/// we would be looking at the length, and for email we should be validating it too"). Numbers can be
/// written the way people write them: 0803 555 1234, 803-555-1234, +234 803 555 1234, 234 803 555 1234,
/// 00234…; a local number is taken to be Nigerian until the workspace keeps its own country. Checks run
/// when the person saves, never while they type.
/// </summary>
public static partial class ContactText
{
    /// <summary>The country a local number (0803…) belongs to.</summary>
    public const string HomeCode = "234";

    public const string EmailAdvice = "That email doesn't look right. Use one like name@business.com.";

    private const string LocalAdvice = "A Nigerian number has 11 digits, such as 0803 555 1234.";
    private const string HomeAdvice = "After +234, a number has 10 digits, such as +234 803 555 1234.";
    private const string AbroadAdvice = "With its country code, a number has 8 to 15 digits, such as +44 20 7946 0958.";
    private const string MobileAdvice = "Nigerian mobile numbers start with 070, 080, 081, 090 or 091.";

    public static PhoneCheck Phone(string? raw)
    {
        var text = Separators().Replace((raw ?? string.Empty).Trim(), string.Empty);
        if (text.Length == 0) return new(null, null, null);
        if (text.StartsWith("00", StringComparison.Ordinal)) text = "+" + text[2..];

        if (text.StartsWith('+'))
        {
            var digits = text[1..];
            if (digits.Length == 0 || !digits.All(char.IsAsciiDigit) || digits[0] == '0') return Wrong(AbroadAdvice);
            if (digits.StartsWith(HomeCode, StringComparison.Ordinal)) return Home(digits[HomeCode.Length..]);
            return digits.Length switch
            {
                < 8 => Short(AbroadAdvice),
                > 15 => Long(AbroadAdvice),
                _ => new(text, null, null),
            };
        }
        if (!text.All(char.IsAsciiDigit)) return Wrong("Use numbers only, such as 0803 555 1234.");

        // Local: a 0 and ten digits, or the ten digits alone (803…), as people write Nigerian numbers.
        if (text[0] == '0')
        {
            if (text.Length < 11) return Short(LocalAdvice);
            if (text.Length > 11) return Long(LocalAdvice);
            return text[1] is '7' or '8' or '9' ? new("+" + HomeCode + text[1..], null, null, Local: true) : Wrong(MobileAdvice);
        }
        if (text.StartsWith(HomeCode, StringComparison.Ordinal) && text.Length > HomeCode.Length + 7) return Home(text[HomeCode.Length..]);
        if (text[0] is '7' or '8' or '9')
        {
            if (text.Length == 10) return new("+" + HomeCode + text, null, null, Local: true);
            if (text.Length < 10) return Short(LocalAdvice);
        }
        return text.Length switch
        {
            < 8 => Short(AbroadAdvice),
            > 15 => Long(AbroadAdvice),
            _ => new("+" + text, null, null),
        };
    }

    /// <summary>A number as it's easy to read: +234 803 555 1234.</summary>
    public static string Display(string number) =>
        number.StartsWith("+" + HomeCode, StringComparison.Ordinal) && number.Length == HomeCode.Length + 11
            ? $"+{HomeCode} {number[4..7]} {number[7..10]} {number[10..]}"
            : number;

    /// <summary>One address: something, @, a domain with a dot and an ending of two letters or more.</summary>
    public static bool IsEmail(string? raw) => EmailShape().IsMatch((raw ?? string.Empty).Trim());

    // After +234 (or 234): ten digits, starting 7, 8 or 9.
    private static PhoneCheck Home(string rest) => rest.Length switch
    {
        < 10 => Short(HomeAdvice),
        > 10 => Long(HomeAdvice),
        _ => rest[0] is '7' or '8' or '9' ? new("+" + HomeCode + rest, null, null) : Wrong(MobileAdvice),
    };

    private static PhoneCheck Short(string advice) => new(null, "Number too short", advice);
    private static PhoneCheck Long(string advice) => new(null, "Number too long", advice);
    private static PhoneCheck Wrong(string advice) => new(null, "Check this number", advice);

    [GeneratedRegex(@"[\s\-().]")]
    private static partial Regex Separators();

    [GeneratedRegex(@"\A[^\s@,;<>()]+@(?:[A-Za-z0-9-]+\.)+[A-Za-z]{2,}\z")]
    private static partial Regex EmailShape();
}
