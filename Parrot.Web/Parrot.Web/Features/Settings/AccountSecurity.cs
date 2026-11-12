namespace Parrot.Web.Features.Settings;

/// <summary>How sign-in codes reach the person (designer, 5 November 2026): by email, the default, or from an
/// authenticator app on their phone. Both can be on (designer, 9 November 2026): when one can't be reached, the other
/// signs in. Sent as "email" / "app".</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum TwoFactorMethod { Email, App }

/// <summary>Current account controls, not an event history. Security events belong in Activity log. Methods: the ways
/// codes come while two-factor is on, one or both; recovery codes come with two-factor whichever way it's on.</summary>
public sealed record AccountSecurityInfo(bool? HasPassword, IReadOnlyList<string> SignInProviders,
    bool TwoFactorEnabled, int RecoveryCodesRemaining, IReadOnlyList<AccountSession> Sessions, IReadOnlyList<TwoFactorMethod>? Methods = null)
{
    public bool Has(TwoFactorMethod method) => Methods?.Contains(method) == true;
}

public sealed record AccountSession(string Id, string Browser, string Device, bool Mobile, bool Current,
    DateTimeOffset LastActiveAt, string? ApproximateLocation = null);

/// <summary>Short-lived setup data from the server. Never put its key or URI in storage, logs or analytics.</summary>
public sealed record AuthenticatorSetup(string Id, string Key, string ProvisioningUri, DateTimeOffset ExpiresAt);
public sealed record TwoFactorResult(AccountSecurityInfo Account, IReadOnlyList<string> RecoveryCodes);
/// <summary>An email code on its way, for turning on email codes: where it went, and until when it works.</summary>
public sealed record EmailCodeSetup(string Id, string Email, DateTimeOffset ExpiresAt);
public enum SecurityField { General, Password, Code }
public sealed class SecurityOperationException(string message, SecurityField field = SecurityField.General, bool restartSetup = false) : Exception(message)
{
    public SecurityField Field { get; } = field;
    public bool RestartSetup { get; } = restartSetup;
}

/// <summary>Production-shaped boundary; no routes exist yet. The server must authenticate, authorise, verify
/// passwords/factors, rate-limit, revoke actual tokens and append Activity log events. UI state is not security.</summary>
public interface IAccountSecurity
{
    bool Available { get; }
    Task<AccountSecurityInfo> ReadAsync(CancellationToken ct = default);
    Task<AuthenticatorSetup> BeginSetupAsync(string currentPassword, CancellationToken ct = default);
    /// <summary>Checks the password and emails a 6-digit code to the account's address; nothing is on yet.</summary>
    Task<EmailCodeSetup> BeginEmailSetupAsync(string currentPassword, CancellationToken ct = default);
    /// <summary>A new code for the same setup; the one before stops working.</summary>
    Task<EmailCodeSetup> ResendEmailCodeAsync(string setupId, CancellationToken ct = default);
    /// <summary>The emailed code: only then are email codes on. Recovery codes come back when this turned two-factor
    /// on; none when the app was already on.</summary>
    Task<TwoFactorResult> ConfirmEmailSetupAsync(string setupId, string code, CancellationToken ct = default);
    /// <summary>The app's code: only then is the app on. Recovery codes as for email.</summary>
    Task<TwoFactorResult> ConfirmSetupAsync(string setupId, string code, CancellationToken ct = default);
    Task CancelSetupAsync(string setupId, CancellationToken ct = default);
    /// <summary>Takes one of two ways off, leaving the other; the last one goes with <see cref="DisableAsync"/>.</summary>
    Task<AccountSecurityInfo> RemoveMethodAsync(string currentPassword, TwoFactorMethod method, CancellationToken ct = default);
    Task<AccountSecurityInfo> DisableAsync(string currentPassword, string code, CancellationToken ct = default);
    Task<TwoFactorResult> ReplaceRecoveryCodesAsync(string currentPassword, string code, CancellationToken ct = default);
    Task RevokeSessionAsync(string sessionId, CancellationToken ct = default);
    Task RevokeOtherSessionsAsync(CancellationToken ct = default);
}

/// <summary>Never invent devices or an Off state when the real account security status is unknown.</summary>
public sealed class UnavailableAccountSecurity : IAccountSecurity
{
    public bool Available => false;
    public Task<AccountSecurityInfo> ReadAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<AuthenticatorSetup> BeginSetupAsync(string password, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<EmailCodeSetup> BeginEmailSetupAsync(string password, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<EmailCodeSetup> ResendEmailCodeAsync(string setupId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<TwoFactorResult> ConfirmEmailSetupAsync(string setupId, string code, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<TwoFactorResult> ConfirmSetupAsync(string setupId, string code, CancellationToken ct = default) => throw new NotSupportedException();
    public Task CancelSetupAsync(string setupId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<AccountSecurityInfo> RemoveMethodAsync(string password, TwoFactorMethod method, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<AccountSecurityInfo> DisableAsync(string password, string code, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<TwoFactorResult> ReplaceRecoveryCodesAsync(string password, string code, CancellationToken ct = default) => throw new NotSupportedException();
    public Task RevokeSessionAsync(string sessionId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task RevokeOtherSessionsAsync(CancellationToken ct = default) => throw new NotSupportedException();
}

public static class SecurityText
{
    // The same three rules as sign-up and password reset (designer, 5 November 2026), within the API's 128 limit.
    public static string? NewPasswordProblem(string current, string next, string confirmation) =>
        string.IsNullOrEmpty(current) ? "Enter your current password."
        : !NewPasswordMeetsRules(next) ? "Choose a password that meets all three requirements."
        : string.IsNullOrEmpty(confirmation) ? "Enter your new password again."
        : next != confirmation ? "The new passwords don't match."
        : next == current ? "Choose a different password." : null;
    public static bool NewPasswordMeetsRules(string next) => next.Length <= 128 && Parrot.Web.Features.Auth.Models.PasswordRules.MeetsAll(next);
    public static bool SixDigits(string code) => code.Length == 6 && code.All(char.IsAsciiDigit);
    public static string SessionWhen(DateTimeOffset at, DateTimeOffset now, TimeZoneInfo zone)
    {
        var elapsed = now - at;
        if (elapsed.TotalMinutes < 1) return "just now";
        if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes} min ago";
        if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours} hr ago";
        return TimeZoneInfo.ConvertTime(at, zone).ToString("d MMM, h:mm tt", System.Globalization.CultureInfo.InvariantCulture);
    }
}
