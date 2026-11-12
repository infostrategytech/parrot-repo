namespace Parrot.Web.Features.Auth.ResetPassword.State;

/// <summary>One-use, circuit-local handoff from the real email link into Settings. Never persists credentials.</summary>
public sealed class PasswordResetHandoff
{
    public static bool IsRecoveryPath(string path) => path.TrimEnd('/').Equals("/reset-password", StringComparison.OrdinalIgnoreCase)
        || path.TrimEnd('/').Equals("/forgot-password", StringComparison.OrdinalIgnoreCase)
        || path.TrimEnd('/').Equals(PasswordResetSignOut.CompletePath, StringComparison.OrdinalIgnoreCase);
    private PasswordResetLink? _pending;
    public static bool IsSameAccount(string? signedInUserId, string? linkUserId) => !string.IsNullOrEmpty(signedInUserId)
        && !string.IsNullOrEmpty(linkUserId) && string.Equals(signedInUserId, linkUserId, StringComparison.Ordinal);
    public void Stage(string userId, string token) => _pending = new(userId, token);
    public PasswordResetLink? Take(string? signedInUserId)
    {
        var link = _pending; _pending = null;
        return link is not null && IsSameAccount(signedInUserId, link.UserId) ? link : null;
    }
}
public sealed record PasswordResetLink(string UserId, string Token)
{
    public override string ToString() => "Password reset link (redacted)";
}
