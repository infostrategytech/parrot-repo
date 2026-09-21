using System.ComponentModel.DataAnnotations;
namespace Parrot.Application.DTOs.Integrations;

public sealed class WhatsAppAccessTokenRequest
{
    [Required, StringLength(8192)]
    public required string AccessToken { get; set; }
}
