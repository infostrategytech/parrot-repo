namespace Parrot.Application.Auth;

/// <summary>
/// Every wording a person can see from the auth endpoints, in one place. The
/// backend owns these strings and the frontend shows them (or overrides by
/// errorCode), so the table the design side signed off on lives here rather
/// than scattered through the service.
/// </summary>
public static class AuthMessages
{
    // ---------------------------------------------------------------- Sign in
    public const string InvalidCredentials =
        "Unable to sign you in. Please check your credentials for errors and try again.";

    public const string AccountLocked =
        "Account is temporarily locked due to too many failed login attempts. Please try again later.";

    // --------------------------------------------------------------- Sign up
    public const string AccountExists =
        "An account already exists with these details.";

    // ---------------------------------------------------------- Verify email
    public const string InvalidCode =
        "That code isn't right or has expired. Check it and try again.";

    public const string TooManyCodes =
        "Too many incorrect codes. Please wait 15 minutes and request a new one.";

    // --------------------------------------------------------- Reset password
    public const string ResetLinkExpired =
        "This reset link has expired.";

    public const string ResetAccountNotFound =
        "We could not find the account this link is for.";

    // -------------------------------------------------------------- Profile
    public const string BusinessNameTooLong =
        "Business name must be between 1 and 256 characters.";

    public const string InvalidDialCode =
        "Choose a country for your phone number.";

    public const string InvalidPhoneNumber =
        "Enter a valid phone number for the country you chose.";

    public const string PhoneNumberTaken =
        "Another account already uses this phone number.";

    public const string NameTooLong =
        "First and last name must be 80 characters or fewer.";

    public const string InvalidLogoUrl =
        "Logo must be a valid URL.";
}
