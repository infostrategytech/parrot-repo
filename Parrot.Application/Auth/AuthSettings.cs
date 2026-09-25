namespace Parrot.Application.Auth;

public class AuthSettings
{
    public const string SectionName = "Auth";

    // NIST SP 800-63B: length is the one composition rule worth having. Twelve
    // characters with no character-class rules is both stronger and easier to
    // type than eight with all four classes. The DTOs share the same floor.
    public int MinimumPasswordLength { get; init; } = PasswordPolicy.MinimumLength;

    public int MaximumPasswordLength { get; init; } = PasswordPolicy.MaximumLength;

    // Have I Been Pwned range lookup. Only the first five hex characters of the
    // password's SHA-1 leave the server, so the password itself never does.
    public bool CheckBreachedPasswords { get; init; } = true;

    // A code every verification accepts in place of the one that would have been
    // emailed, so a local stack needs no mail server to finish a sign-up. Read
    // only when the host runs as Development; the API refuses to start with it
    // set anywhere else.
    public string? DevelopmentVerificationCode { get; init; }
}
