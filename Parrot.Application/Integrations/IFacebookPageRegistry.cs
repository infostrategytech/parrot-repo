namespace Parrot.Application.Integrations;

/// <summary>
/// Resolves which tenant owns a given Facebook Page ID.
/// Each SaaS customer links their own Facebook Page; this registry maps
/// Page ID → tenant so webhook events can be routed to the correct account.
/// </summary>
public sealed record FacebookPageRegistration(Guid ConnectionId, string OwnerUserId, string PageId, string PageName);

public interface IFacebookPageRegistry
{
    Task<FacebookPageRegistration?> ResolveAsync(string pageId, CancellationToken cancellationToken = default);
}
