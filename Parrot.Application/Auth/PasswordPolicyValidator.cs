using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Parrot.Application.Logging;
using Parrot.Application.Models;

namespace Parrot.Application.Auth;

// What Identity's built-in rules do not cover once character-class rules are
// off: an upper bound, nothing lifted from the account itself, the offline
// common list, and the breach lookup. Runs after Identity's length check.
public class PasswordPolicyValidator : IPasswordValidator<ApplicationUser>
{
    private readonly IPasswordBreachChecker _breachChecker;
    private readonly AuthSettings _settings;
    private readonly IAppLogger<PasswordPolicyValidator> _logger;

    public PasswordPolicyValidator(
        IPasswordBreachChecker breachChecker,
        IOptions<AuthSettings> settings,
        IAppLogger<PasswordPolicyValidator> logger)
    {
        _breachChecker = breachChecker;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<IdentityResult> ValidateAsync(UserManager<ApplicationUser> manager, ApplicationUser user, string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return IdentityResult.Failed(Error("PasswordEmpty", "Enter a password."));
        }

        if (password.Length > _settings.MaximumPasswordLength)
        {
            return IdentityResult.Failed(Error("PasswordTooLong", $"Use at most {_settings.MaximumPasswordLength} characters."));
        }

        if (ContainsAccountDetails(password, user))
        {
            return IdentityResult.Failed(Error("PasswordContainsAccountDetails", "Your password can't contain your email address or business name."));
        }

        if (CommonPasswords.IsCommon(password))
        {
            return IdentityResult.Failed(Error("PasswordTooCommon", "That password is too common. Try a few unrelated words instead."));
        }

        if (_settings.CheckBreachedPasswords)
        {
            try
            {
                if (await _breachChecker.IsBreachedAsync(password))
                {
                    return IdentityResult.Failed(Error("PasswordBreached", "That password has appeared in a data breach. Choose a different one."));
                }
            }
            catch (Exception ex)
            {
                // The lookup is a courtesy to the person, not a gate: a network
                // fault must not stop anyone from setting a password.
                _logger.LogWarning(ex, "Breached-password lookup unavailable; the password was accepted without it");
            }
        }

        return IdentityResult.Success;
    }

    private static bool ContainsAccountDetails(string password, ApplicationUser user)
    {
        string? email = user.Email;
        if (!string.IsNullOrEmpty(email))
        {
            if (password.Contains(email, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string localPart = email.Split('@')[0];
            if (localPart.Length >= 4 && password.Contains(localPart, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        string? business = user.BusinessName?.Trim();
        return !string.IsNullOrEmpty(business)
            && business.Length >= 4
            && password.Contains(business, StringComparison.OrdinalIgnoreCase);
    }

    private static IdentityError Error(string code, string description) =>
        new IdentityError { Code = code, Description = description };
}
