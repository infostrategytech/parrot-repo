namespace Parrot.Application.BusinessSources;

/// <summary>Metadata returned from the Meta Graph API for a WhatsApp Business profile.</summary>
public record WhatsAppProfileData(
    string? VerifiedName,
    string? DisplayPhoneNumber,
    string? About,
    string? Description,
    string? Address,
    string? Email,
    string? Vertical,
    string? ProfilePictureUrl,
    IReadOnlyList<string> Websites);
