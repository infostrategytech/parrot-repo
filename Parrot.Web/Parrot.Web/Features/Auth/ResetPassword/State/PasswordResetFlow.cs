using System.Net;
using Parrot.Web.Features.Auth.Models;
using Parrot.Web.Features.Auth.ResetPassword.Models;
using Parrot.Web.Features.Auth.ResetPassword.Services;
using Parrot.Web.Services;

namespace Parrot.Web.Features.Auth.ResetPassword.State;

/// <summary>The existing email-link reset contract, shared by the signed-out page and Settings modal.
/// Tokens remain in this short-lived state only; no storage, logs, synthetic verification or current-session bypass.</summary>
public sealed class PasswordResetFlow(IResetPasswordService service, string? userId, string? token)
{
    private readonly string? _userId = userId, _token = token;
    public string Password { get; private set; } = "";
    public string Confirmation { get; private set; } = "";
    public string? PasswordError { get; private set; }
    public string? ConfirmationError { get; private set; }
    public string? Error { get; private set; } = string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(token) ? "This reset link is incomplete." : null;
    public bool LinkSpent { get; private set; } = string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(token);
    public bool Busy { get; private set; }
    public bool Done { get; private set; }
    public int FocusRevision { get; private set; }
    public void SetPassword(string? value) { if (Busy) return; Password = value ?? ""; PasswordError = ConfirmationError = null; if (!LinkSpent) Error = null; }
    public void SetConfirmation(string? value) { if (Busy) return; Confirmation = value ?? ""; ConfirmationError = null; if (!LinkSpent) Error = null; }
    public void ClearSecrets() { Password = Confirmation = ""; }
    public async Task SubmitAsync()
    {
        if (Busy || Done || LinkSpent) return;
        Error = null;
        PasswordError = Password.Length <= 128 && PasswordRules.MeetsAll(Password) ? null : "Choose a password that meets all three requirements.";
        ConfirmationError = Confirmation.Length == 0 ? "Enter your new password again."
            : Confirmation != Password ? "The two passwords do not match." : null;
        if (PasswordError is not null || ConfirmationError is not null) { FocusRevision++; return; }
        Busy = true;
        try
        {
            await service.ResetPasswordAsync(new ResetPasswordRequest { UserId = _userId!, Token = _token!, NewPassword = Password, ConfirmNewPassword = Confirmation });
            ClearSecrets(); Done = true;
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.BadRequest && IsLinkProblem(ex))
        { RejectLink("This reset link has expired or has already been used."); }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.BadRequest && ex.Errors.Count > 0)
        { PasswordError = ex.UserMessage; FocusRevision++; }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
        { RejectLink("This reset link has expired."); }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        { RejectLink("We could not find the account this link is for."); }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        { Error = "Too many attempts. Wait a few minutes, then try again."; }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        { Error = "We could not reach yára. Check your internet connection and try again."; }
        catch (OperationCanceledException)
        { Error = "The password change didn't finish. Try again."; }
        catch (Exception)
        { Error = "Something went wrong on our side. Try again in a moment."; }
        finally { Busy = false; }
    }
    private void RejectLink(string message) { ClearSecrets(); Error = message; LinkSpent = true; }
    private static bool IsLinkProblem(ApiException error) => error.UserMessage.Contains("token", StringComparison.OrdinalIgnoreCase)
        || error.UserMessage.Contains("link", StringComparison.OrdinalIgnoreCase)
        || (error.ErrorCode?.Contains("token", StringComparison.OrdinalIgnoreCase) ?? false);
}
