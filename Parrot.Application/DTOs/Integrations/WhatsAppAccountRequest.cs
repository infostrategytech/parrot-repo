using System.ComponentModel.DataAnnotations;

namespace Parrot.Application.DTOs.Integrations;

public sealed class WhatsAppAccountRequest
{
    [Required, RegularExpression("^[0-9]{1,64}$")]
    public required string WhatsAppBusinessAccountId { get; set; }

    [Required, RegularExpression("^[0-9]{1,64}$")]
    public required string PhoneNumberId { get; set; }
}
