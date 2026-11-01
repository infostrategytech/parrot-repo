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

    /// <summary>The workspace the person always opens on sign-in, or null when they resume where they left off.</summary>
    Task<string?> GetDefaultWorkspaceAsync(string userId);

    /// <summary>
    /// Sets "When I sign in": a workspace to always open, which the person has to
    /// own or have access to, or null to resume where they left off.
    /// </summary>
    Task SetDefaultWorkspaceAsync(string userId, string? workspaceId);
}
