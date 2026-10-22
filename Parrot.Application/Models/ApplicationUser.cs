using Microsoft.AspNetCore.Identity;

namespace Parrot.Application.Models;

public class ApplicationUser : IdentityUser
{
    // The business name and logo now live on the workspace. These two columns
    // are only read once, to name a tenant's first workspace, and go in a later
    // migration once every account has one.
    public string? BusinessName { get; set; }

    public string? FirstName { get; set; }

    public string? LastName { get; set; }

    public string? LogoUrl { get; set; }

    // A phone is kept in two parts: the country's dial code and the national
    // number, e.g. 234 and 8012345678. Sign-in matches on both, so a person can
    // type 08012345678 or 2348012345678 and reach the same row.
    public string? PhoneDialCode { get; set; }

    /// <summary>The workspace switched to last, where the next sign-in lands unless a default is set. Null before the first.</summary>
    public string? LastWorkspaceId { get; set; }

    /// <summary>
    /// "When I sign in": null resumes where the person left off (<see cref="LastWorkspaceId"/>);
    /// a workspace id always opens that one, while they can still reach it.
    /// </summary>
    public string? DefaultWorkspaceId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }
}
