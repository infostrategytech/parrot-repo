using Microsoft.AspNetCore.Identity;
using Parrot.Application.Auth;
using Parrot.Application.DTOs.Auth;
using Parrot.Application.Logging;
using Parrot.Application.Models;
using Parrot.Domain.Exceptions;

namespace Parrot.Application.Profile;

public class ProfileService : IProfileService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IAppLogger<ProfileService> _logger;

    public ProfileService(
        UserManager<ApplicationUser> userManager,
        IAppLogger<ProfileService> logger)
    {
        _userManager = userManager;
        _logger = logger;
    }

    public async Task<ProfileResponse> GetAsync(string userId)
    {
        ApplicationUser user = await FindOrThrowAsync(userId);
        return ToResponse(user);
    }

    public async Task<ProfileResponse> UpdateAsync(string userId, UpdateProfileRequest request)
    {
        ApplicationUser user = await FindOrThrowAsync(userId);

        if (request.BusinessName is not null)
        {
            string name = request.BusinessName.Trim();
            if (name.Length is < 1 or > 256)
            {
                throw new ValidationException(AuthMessages.BusinessNameTooLong);
            }

            user.BusinessName = name;
        }

        if (request.FirstName is not null)
        {
            user.FirstName = ToOptionalName(request.FirstName);
        }

        if (request.LastName is not null)
        {
            user.LastName = ToOptionalName(request.LastName);
        }

        // The URL is kept on the account as it arrives. Where the file itself
        // lives is the upload endpoint's business, not this one's. An empty
        // string is the image being removed.
        if (request.LogoUrl is not null)
        {
            user.LogoUrl = ToOptionalLogoUrl(request.LogoUrl);
        }

        if (request.CountryCode is not null || request.Number is not null)
        {
            ApplyPhone(user, request);
        }

        IdentityResult result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            _logger.LogWarning(
                "Profile update failed for {UserId}: {Errors}",
                userId,
                string.Join("; ", result.Errors.Select(e => e.Code)));

            throw new ValidationException(result.Errors.Select(e => e.Description));
        }

        using (_logger.BeginEntityScope("User", user.Id))
        {
            _logger.LogInformation("Profile updated for {UserId}", userId);
        }

        return ToResponse(user);
    }

    private static ProfileResponse ToResponse(ApplicationUser user) =>
        new(
            user.Id,
            user.Email ?? string.Empty,
            user.FirstName,
            user.LastName,
            user.BusinessName,
            user.LogoUrl,
            PhoneNormalizer.ToDisplay(user.PhoneDialCode, user.PhoneNumber),
            user.BusinessName is not null);

    // Blank means cleared, so a name can be removed as well as changed.
    private static string? ToOptionalName(string value)
    {
        string name = value.Trim();
        if (name.Length > 80)
        {
            throw new ValidationException(AuthMessages.NameTooLong);
        }

        return name.Length == 0 ? null : name;
    }

    private static string? ToOptionalLogoUrl(string value)
    {
        string url = value.Trim();
        if (url.Length == 0)
        {
            return null;
        }

        bool isWebUrl = Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

        return isWebUrl ? url : throw new ValidationException(AuthMessages.InvalidLogoUrl);
    }

    private void ApplyPhone(ApplicationUser user, UpdateProfileRequest request)
    {
        // The country is a stated field, so it can be checked on its own terms
        // before the number is looked at at all.
        string dialCode = new((request.CountryCode ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        if (dialCode.Length is < 1 or > 3 || dialCode[0] == '0')
        {
            throw new ValidationException(AuthMessages.InvalidDialCode);
        }

        // A plus writes the country out loud, so it has to be the country
        // chosen above. Without this, a number from elsewhere would be stored
        // as if it belonged to the picker's country, and the person would see —
        // and be recognised by — the wrong number.
        string typed = request.Number?.Trim() ?? string.Empty;
        if (typed.StartsWith("+", StringComparison.Ordinal)
            && !typed.AsSpan(1).StartsWith(dialCode, StringComparison.Ordinal))
        {
            throw new ValidationException(AuthMessages.InvalidPhoneNumber);
        }

        string? national = PhoneNormalizer.ToNational(request.Number, dialCode);
        if (national is null)
        {
            throw new ValidationException(AuthMessages.InvalidPhoneNumber);
        }

        // Sign-in with a local number searches the national column on its own,
        // so that column identifies the account by itself and can never be
        // shared. Checked here rather than left to the database so the answer is
        // a clear message instead of a constraint error.
        bool taken = _userManager.Users.Any(u => u.Id != user.Id && u.PhoneNumber == national);

        if (taken)
        {
            throw new ConflictException(AuthMessages.PhoneNumberTaken);
        }

        user.PhoneDialCode = dialCode;
        user.PhoneNumber = national;
    }

    private async Task<ApplicationUser> FindOrThrowAsync(string userId)
    {
        ApplicationUser? user = await _userManager.FindByIdAsync(userId);
        if (user is null)
        {
            _logger.LogWarning("Profile lookup for unknown UserId: {UserId}", userId);
            throw new NotFoundException("Account not found.");
        }

        return user;
    }
}
