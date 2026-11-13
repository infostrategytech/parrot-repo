namespace Parrot.Application.BusinessSources;

public interface IInstagramProfileClient
{
    /// <summary>
    /// Fetches the Instagram business profile for the given IG user ID.
    /// Returns null when the Graph API returns an error or the account is not a business account.
    /// </summary>
    Task<InstagramProfileData?> GetProfileAsync(string igUserId, string accessToken, CancellationToken ct);
}
