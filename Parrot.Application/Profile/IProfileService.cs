using Parrot.Application.DTOs.Auth;

namespace Parrot.Application.Profile;

/// <summary>
/// The account's own details — names and phone number — with the business
/// name and logo of the workspace the session works in.
/// </summary>
public interface IProfileService
{
    Task<ProfileResponse> GetAsync(string userId, string workspaceId);

    /// <summary>
    /// Saves what the setup step or the settings page sends. A null field is
    /// left as it is, so a partial save cannot wipe something that was already
    /// entered; an empty name or logo URL clears that field.
    /// </summary>
    Task<ProfileResponse> UpdateAsync(string userId, string workspaceId, UpdateProfileRequest request);
}
