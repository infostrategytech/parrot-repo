namespace Parrot.Application.BusinessSources;

public interface IWhatsAppProfileClient
{
    /// <summary>
    /// Fetches the WhatsApp Business Profile for the given phone number ID.
    /// Returns null when the API returns an error (token expired, permission denied, etc.).
    /// </summary>
    Task<WhatsAppProfileData?> GetProfileAsync(string phoneNumberId, string accessToken, CancellationToken ct);
}
