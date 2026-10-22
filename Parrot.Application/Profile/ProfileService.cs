using Microsoft.AspNetCore.Identity;
using Parrot.Application.Auth;
using Parrot.Application.DTOs.Auth;
using Parrot.Application.Logging;
using Parrot.Application.Models;
using Parrot.Application.Teams;
using Parrot.Application.DTOs.Workspaces;
using Parrot.Application.Workspaces;
using Parrot.Domain.Entities;
using Parrot.Domain.Exceptions;

namespace Parrot.Application.Profile;

public class ProfileService : IProfileService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IWorkspaceService _workspaces;
    private readonly IAppLogger<ProfileService> _logger;

    public ProfileService(
        UserManager<ApplicationUser> userManager,
        IWorkspaceService workspaces,
        IAppLogger<ProfileService> logger)
    {
        _userManager = userManager;
        _workspaces = workspaces;
        _logger = logger;
    }

    public async Task<ProfileResponse> GetAsync(string userId, string workspaceId)
    {
        ApplicationUser user = await FindOrThrowAsync(userId);
        WorkspaceAccess access = await _workspaces.GetAccessibleAsync(userId, workspaceId);
        return ToResponse(user, access.Workspace);
    }

    public async Task<ProfileResponse> UpdateAsync(string userId, string workspaceId, UpdateProfileRequest request)
    {
        ApplicationUser user = await FindOrThrowAsync(userId);

        if (request.FirstName is not null)
        {
            user.FirstName = ToOptionalName(request.FirstName);
        }

        if (request.LastName is not null)
        {
            user.LastName = ToOptionalName(request.LastName);
        }

        if (request.CountryCode is not null || request.Number is not null)
        {
            ApplyPhone(user, request);
        }

        // The business name and logo are the workspace's; the rest is the person's.
        // The person's fields have all been checked by now, so a bad phone number
        // is refused before the workspace is touched.
        if (request.BusinessName is not null || request.LogoUrl is not null)
        {
            WorkspaceAccess access = await _workspaces.GetAccessibleAsync(userId, workspaceId);
            if (!access.IsOwner)
            {
                throw new ForbiddenException("Only the workspace owner can change its name or logo.");
            }

            await _workspaces.UpdateAsync(userId, workspaceId, new UpdateWorkspaceRequest
            {
                Name = request.BusinessName,
                LogoUrl = request.LogoUrl,
            });
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

        return ToResponse(user, (await _workspaces.GetAccessibleAsync(userId, workspaceId)).Workspace);
    }

    private static ProfileResponse ToResponse(ApplicationUser user, Workspace workspace) =>
        new(
            user.Id,
            user.Email ?? string.Empty,
            user.FirstName,
            user.LastName,
            workspace.Name,
            workspace.LogoUrl,
            PhoneNormalizer.ToDisplay(user.PhoneDialCode, user.PhoneNumber),
            workspace.Name is not null);

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

    public async Task<string?> GetDefaultWorkspaceAsync(string userId) =>
        (await FindOrThrowAsync(userId)).DefaultWorkspaceId;

    public async Task SetDefaultWorkspaceAsync(string userId, string? workspaceId)
    {
        ApplicationUser user = await FindOrThrowAsync(userId);

        // Not found unless they can reach it, the same answer as switching gives.
        if (workspaceId is not null)
        {
            await _workspaces.GetAccessibleAsync(userId, workspaceId);
        }

        if (user.DefaultWorkspaceId == workspaceId)
        {
            return;
        }

        user.DefaultWorkspaceId = workspaceId;
        IdentityResult result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            throw new ValidationException(result.Errors.Select(e => e.Description));
        }

        using (_logger.BeginEntityScope("User", user.Id))
        {
            _logger.LogInformation(workspaceId is null
                ? "Sign-in set to resume the last workspace"
                : "Sign-in set to always open workspace {WorkspaceId}", workspaceId);
        }
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
