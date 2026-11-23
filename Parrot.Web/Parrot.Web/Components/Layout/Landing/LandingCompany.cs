namespace Parrot.Web.Components.Layout.Landing;

/// <summary>Who owns yára, said the same way on the sign-in page, its footer and the public pages, so Meta's
/// reviewers see one owner everywhere (designer, 17 November 2026). The owner is Infostrategy Technology Nigeria, as
/// the reviewed Privacy Policy and the company's own About page name it (designer, 23 November 2026; it was
/// "Infostrategy Technology Limited", which neither source uses); the address is the one on its own site
/// (istrategytech.com). The legal name and address must match the Meta Business account exactly. yára goes live on
/// yarahq.com, at the same address as Infostrategy (designer, 18 November 2026). The phone number is a placeholder,
/// to be replaced after review. Change them here only.</summary>
public static class LandingCompany
{
    public const string Name = "Infostrategy Technology Nigeria";
    public const string Website = "https://istrategytech.com";
    public const string Address = "4 Madiana Close, off Dar-Es-Salam Street, Wuse 2, Abuja, Nigeria";
    public const string City = "Abuja, Nigeria";
    public const string Email = "support@yarahq.com";
    /// <summary>The business phone Meta's verification asks for. A placeholder until the real line is confirmed.</summary>
    public const string Phone = "+234 800 000 0000";
    public const string PhoneLink = "tel:+2348000000000";
    /// <summary>Where yára lives, the website Meta checks against the Business account.</summary>
    public const string SiteUrl = "https://yarahq.com";
    public const string MarketingTitle = "yára · One inbox. An AI teammate. More time for your business.";
    public const string MarketingDescription = "Bring your customer chats into one inbox. An AI teammate answers everyday questions, captures orders and bookings, and brings you in when needed.";
    /// <summary>The company's mark, one colour, shown in the text colour of wherever it sits.</summary>
    public const string Logo = "/brand/infostrategy-logo.svg";
    /// <summary>The yára app's store pages. Not published yet: null shows "Coming soon" in the Download app menu.</summary>
    public const string? AppStoreUrl = null;
    public const string? PlayStoreUrl = null;
    /// <summary>When the public documents last changed; shown on each one.</summary>
    public static readonly DateOnly DocumentsUpdated = new(2026, 11, 17);
}
