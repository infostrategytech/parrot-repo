namespace Parrot.Application.BusinessSources;

public interface IFacebookPageClient
{
    /// <summary>
    /// Fetches Facebook Page details for the given page ID.
    /// Returns null when the Graph API returns an error or the page is inaccessible.
    /// </summary>
    Task<FacebookPageData?> GetPageAsync(string pageId, string accessToken, CancellationToken ct);
}
