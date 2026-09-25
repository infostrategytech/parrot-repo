namespace Parrot.Application.Auth;

// The bounds the request DTOs compile against; AuthSettings defaults to the
// same values so the two cannot drift apart silently.
public static class PasswordPolicy
{
    public const int MinimumLength = 12;

    public const int MaximumLength = 128;
}
