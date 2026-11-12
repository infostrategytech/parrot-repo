using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Parrot.Web.Features.Auth.Models;
using Parrot.Web.Features.Settings;

namespace Parrot.Web.Features.Auth.TwoFactor;

/// <summary>
/// The second step of signing in, for an account with two-factor authentication on (designer, 9 November 2026: "it
/// should be active when signing into the account too"; Google and Facebook sign-ins ask for it as well). The API
/// answers a right password with this instead of a session; the session comes once the code is checked. The API
/// doesn't do this yet (settings-backend-requests): it never sends one, so real sign-in is unchanged until it does.
/// </summary>
/// <param name="Id">The sign-in waiting for its code. Short-lived.</param>
/// <param name="Method">The way asked for first: the authenticator app when it's on (no wait for an email), else an
/// emailed code, already sent.</param>
/// <param name="Email">The account's address, to say where an emailed code went (masked on screen).</param>
/// <param name="CanUseRecoveryCode">True when the account has recovery codes left.</param>
/// <param name="Methods">Every way that's on, so the page offers the others ("Email me a code instead").</param>
public sealed record TwoFactorChallenge(
    string Id,
    TwoFactorMethod Method,
    string Email,
    DateTime ExpiresAt,
    bool CanUseRecoveryCode,
    IReadOnlyList<TwoFactorMethod>? Methods = null)
{
    public IReadOnlyList<TwoFactorMethod> Ways => Methods is { Count: > 0 } ways ? ways : [Method];
}

/// <param name="Session">The signed-in session, as a plain sign-in returns it.</param>
/// <param name="RememberedDevice">The token this browser keeps and sends with its next sign-ins, so the code isn't
/// asked for again on it for 30 days. Always given, nothing to tick (designer, 9 November 2026: "a silent hidden
/// function").</param>
/// <param name="RecoveryCodesLeft">After a recovery code: how many are left.</param>
public sealed record TwoFactorSignInResult(AuthResponse Session, string? RememberedDevice = null, int? RecoveryCodesLeft = null);

/// <summary>
/// WrongCode: try again. TooManyAttempts and SignInExpired: nothing typed here can work, start the sign-in again.
/// CodeExpired: an emailed code ran out; a new one is a tap away.
/// </summary>
public enum TwoFactorProblem { WrongCode, TooManyAttempts, CodeExpired, SignInExpired }

/// <summary>Why a code didn't sign in, in words to show under the boxes.</summary>
public sealed class TwoFactorSignInException(string message, TwoFactorProblem problem) : Exception(message)
{
    public TwoFactorProblem Problem { get; } = problem;
}

/// <summary>Checks the second step and hands back the session.</summary>
public interface ITwoFactorSignIn
{
    /// <summary>A code from the authenticator app or the email, whichever is being used.</summary>
    Task<TwoFactorSignInResult> VerifyAsync(string challengeId, string code, CancellationToken ct = default);

    Task<TwoFactorSignInResult> RecoverAsync(string challengeId, string recoveryCode, CancellationToken ct = default);

    /// <summary>Emails a code: when switching to email from the app, or again. The one before stops working.</summary>
    Task SendEmailCodeAsync(string challengeId, CancellationToken ct = default);
}

/// <summary>The API has no second step yet, so it never sends a challenge and this is never reached.</summary>
public sealed class UnavailableTwoFactorSignIn : ITwoFactorSignIn
{
    public Task<TwoFactorSignInResult> VerifyAsync(string challengeId, string code, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<TwoFactorSignInResult> RecoverAsync(string challengeId, string recoveryCode, CancellationToken ct = default) => throw new NotSupportedException();
    public Task SendEmailCodeAsync(string challengeId, CancellationToken ct = default) => throw new NotSupportedException();
}

/// <summary>
/// A recovery code out of whatever was pasted (designer, 9 November 2026: a code pasted as copied didn't work). Codes
/// are 12 letters and numbers, shown as two groups of six; spaces, line breaks, dashes, case and list numbers around
/// it don't matter. A pasted list of several says so, rather than guessing which one to use.
/// </summary>
public static partial class RecoveryCodeText
{
    public const string Example = "8B966D-41E4C8";

    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Za-z0-9]{6}[\s-]?[A-Za-z0-9]{6}(?![A-Za-z0-9])")]
    private static partial Regex Spaced();

    [GeneratedRegex(@"[A-Za-z0-9]{6}-[A-Za-z0-9]{6}")]
    private static partial Regex Dashed();

    /// <summary>The one code in <paramref name="text"/>, as "XXXXXX-XXXXXX", or the problem to show.</summary>
    public static (string? Code, string? Problem) Read(string? text)
    {
        var value = text?.Trim() ?? "";
        if (value.Length == 0) return (null, "Enter one of your recovery codes.");

        // Codes pasted one after another lose their line breaks in some fields: dashed codes still split apart.
        var dashed = Dashed().Matches(value);
        var found = dashed.Count > 1 ? dashed.Select(match => match.Value).ToList() : Spaced().Matches(value).Select(match => match.Value).ToList();
        if (found.Count > 1) return (null, $"That's {found.Count} codes. Keep just one, then verify.");
        if (found.Count == 0) return (null, $"Recovery codes are 12 letters and numbers, like {Example}.");

        var raw = new string([.. found[0].Where(char.IsAsciiLetterOrDigit)]).ToUpperInvariant();
        return ($"{raw[..6]}-{raw[6..]}", null);
    }
}

/// <summary>The sign-in waiting for its code, between the sign-in page (or Google's return) and the code page.</summary>
public sealed class TwoFactorSignInState
{
    public TwoFactorChallenge? Pending { get; private set; }

    public void Start(TwoFactorChallenge challenge) => Pending = challenge;

    public void Clear() => Pending = null;
}

/// <summary>
/// The token the API gave this browser after its second step, per account, kept with the browser's other protected
/// storage and sent with the next sign-in, so the code isn't asked for again on this device for 30 days.
/// </summary>
public sealed class RememberedDeviceStorage(ProtectedLocalStorage storage)
{
    private static string Key(string email) => "remembered-device:" + email.Trim().ToLowerInvariant();

    public async Task<string?> ReadAsync(string email)
    {
        try
        {
            var result = await storage.GetAsync<string>(Key(email));
            return result.Success ? result.Value : null;
        }
        catch
        {
            // Unreadable storage only means the code is asked for again.
            return null;
        }
    }

    public async Task SaveAsync(string email, string token)
    {
        try { await storage.SetAsync(Key(email), token); }
        catch { /* Not remembered: the code is asked for next time, which is safe. */ }
    }
}
