using System.ComponentModel.DataAnnotations;

namespace Parrot.Application.DTOs.Integrations;

public class CreateWhatsAppConnectionRequest
{
    [Required, StringLength(128)]
    public string Name { get; set; } = "WhatsApp Business";

    [StringLength(64)]
    public string? WhatsAppBusinessAccountId { get; set; }

    [StringLength(64)]
    public string? PhoneNumberId { get; set; }

    [StringLength(32)]
    public string? DisplayPhoneNumber { get; set; }
}
