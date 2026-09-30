namespace Parrot.Web.Features.Auth.Models;

/// <summary>
/// What a new password has to meet. Defined once, because Sign Up and
/// password reset must agree. The API only enforces the length (8 to 128);
/// the mixed case and number rules are this form's own.
/// </summary>
public static class PasswordRules
{
    public const int MinimumLength = 8;

    public static bool HasMinimumLength(string? password) => password?.Length >= MinimumLength;

    public static bool HasMixedCase(string? password) =>
        password?.Any(char.IsUpper) == true && password.Any(char.IsLower);

    public static bool HasNumber(string? password) => password?.Any(char.IsDigit) == true;

    public static bool MeetsAll(string? password) =>
        HasMinimumLength(password) && HasMixedCase(password) && HasNumber(password);

    /// <summary>Not required. Only moves the meter from Good to Strong.</summary>
    public static bool HasExtraStrength(string? password) =>
        password?.Length >= 12 || password?.Any(character => !char.IsLetterOrDigit(character)) == true;

    public static int Score(string? password) =>
        (HasMinimumLength(password) ? 1 : 0) +
        (HasMixedCase(password) ? 1 : 0) +
        (HasNumber(password) ? 1 : 0) +
        (HasExtraStrength(password) ? 1 : 0);
}
