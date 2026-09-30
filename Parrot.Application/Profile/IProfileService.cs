using Parrot.Application.DTOs.Auth;

namespace Parrot.Application.Profile;

/// <summary>The account's own details: names, business name, logo and phone number.</summary>
public interface IProfileService
{
    Task<ProfileResponse> GetAsync(string userId);

    /// <summary>
    /// Saves what the setup step or the settings page sends. A null field is
    /// left as it is, so a partial save cannot wipe something that was already
    /// entered; an empty name or logo URL clears that field.
    /// </summary>
    Task<ProfileResponse> UpdateAsync(string userId, UpdateProfileRequest request);
}
