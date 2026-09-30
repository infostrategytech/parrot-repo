using Microsoft.AspNetCore.Identity;

namespace Parrot.Application.Models;

public class ApplicationUser : IdentityUser
{
    public string? BusinessName { get; set; }

    public string? FirstName { get; set; }

    public string? LastName { get; set; }

    public string? LogoUrl { get; set; }

    // A phone is kept in two parts: the country's dial code and the national
    // number, e.g. 234 and 8012345678. Sign-in matches on both, so a person can
    // type 08012345678 or 2348012345678 and reach the same row.
    public string? PhoneDialCode { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }
}
